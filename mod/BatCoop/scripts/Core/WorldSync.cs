#nullable enable
// Shared story and world state, host-authoritative (coop-anything playbook/effect-capture-story-sync.md):
// - the host reads its whole tracked state a few times a second and commits every difference as one numbered
//   transaction of primitive effects; guests apply transactions strictly in order and never twice;
// - a guest whose own script tries to change shared state sends an intent instead; the host applies it, so the
//   change (a mission starting, a door opening) happens for everyone through the host's next transaction;
// - joiners get a snapshot; guests send a digest every few seconds and the host repairs any mismatch.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BatCoop.Core;

public enum WorldValueKind : byte { Flag = 1, Int = 2, Str = 3 }

/// <summary>One primitive change. Keys: "g:Name" global flag, "o:ObjectPath|Name" object flag, "ch:main" etc. for
/// chapters, "obj:primary"/"obj:side" objectives, "lvl:map" the host's map.</summary>
public readonly record struct WorldEffect(WorldValueKind Kind, string Key, int Int, string Str)
{
    public static WorldEffect Flag(string key, bool v) => new(WorldValueKind.Flag, key, v ? 1 : 0, "");
    public static WorldEffect Number(string key, int v) => new(WorldValueKind.Int, key, v, "");
    public static WorldEffect Text(string key, string v) => new(WorldValueKind.Str, key, 0, v);
    public bool IsSet => Kind == WorldValueKind.Flag && Int != 0;

    public void Write(WireWriter w)
    {
        w.U8((byte)Kind).Str(Key);
        if (Kind == WorldValueKind.Str) w.Str(Str); else w.U32((uint)Int);
    }

    public static WorldEffect Read(ref WireReader r)
    {
        var kind = (WorldValueKind)r.U8();
        var key = r.Str();
        return kind == WorldValueKind.Str ? new(kind, key, 0, r.Str()) : new(kind, key, (int)r.U32(), "");
    }
}

/// <summary>The tracked state: one value per key. Flags that are false are kept too (a cleared flag is state).</summary>
public sealed class WorldState
{
    public readonly SortedDictionary<string, WorldEffect> Values = new(StringComparer.Ordinal);

    public void Set(in WorldEffect e) => Values[e.Key] = e;

    public WorldState Clone()
    {
        var c = new WorldState();
        foreach (var kv in Values) c.Values[kv.Key] = kv.Value;
        return c;
    }

    /// <summary>Effects that turn <paramref name="from"/> into this state. Keys missing here are reported as
    /// cleared flags (a flag that disappeared from the set list was cleared).</summary>
    public List<WorldEffect> DiffFrom(WorldState from)
    {
        var d = new List<WorldEffect>();
        foreach (var (k, v) in Values)
            if (!from.Values.TryGetValue(k, out var old) || old != v) d.Add(v);
        foreach (var (k, old) in from.Values)
            if (!Values.ContainsKey(k) && old.Kind == WorldValueKind.Flag && old.Int != 0) d.Add(WorldEffect.Flag(k, false));
        return d;
    }

    /// <summary>Order-independent 64-bit FNV-1a over the canonical form; cleared flags count as absent.</summary>
    public ulong Hash()
    {
        ulong h = 1469598103934665603UL;
        foreach (var (k, v) in Values)
        {
            if (v.Kind == WorldValueKind.Flag && v.Int == 0) continue;
            foreach (var ch in k) { h ^= ch; h *= 1099511628211UL; }
            h ^= (byte)v.Kind; h *= 1099511628211UL;
            h ^= (uint)v.Int; h *= 1099511628211UL;
            foreach (var ch in v.Str) { h ^= ch; h *= 1099511628211UL; }
        }
        return h;
    }

    public int CountSet => Values.Values.Count(v => v.Kind != WorldValueKind.Flag || v.Int != 0);

    /// <summary>Hash of this (guest) state as the host would see it: only keys the host tracks, plus flags we have
    /// set that the host does not. Numbers or text only this machine tracks are not drift.</summary>
    public ulong HashAgainst(WorldState host)
    {
        var t = new WorldState();
        foreach (var k in host.Values.Keys)
            if (Values.TryGetValue(k, out var v)) t.Set(v);
        foreach (var (k, v) in Values)
            if (!host.Values.ContainsKey(k) && v.Kind == WorldValueKind.Flag && v.Int != 0) t.Set(v);
        return t.Hash();
    }
}

/// <summary>What the session needs from a game (or from the lab's fake world).</summary>
public interface IWorldPort
{
    /// <summary>The local copy's current tracked state. Null while there is no world (menus, loading).</summary>
    WorldState? Read();

    /// <summary>Set one value directly. Called with the echo guard up, so the game's change hooks must not turn it
    /// into an intent.</summary>
    void Apply(in WorldEffect e);
}

public enum WorldMsg : byte { Txn = 1, Snapshot = 2, Intent = 3, Digest = 4, SnapshotRequest = 5 }

public sealed class WorldSync
{
    readonly Action<string> _log;
    readonly Action<byte, bool, byte[]> _send; // to, reliable, payload
    readonly List<(uint seq, List<WorldEffect> fx)> _hostLog = [];
    readonly SortedDictionary<uint, List<WorldEffect>> _pending = [];
    WorldState _committed = new();
    uint _seq;            // host: last committed; guest: last applied
    uint _intentSeq;
    readonly Dictionary<string, (WorldEffect e, double at)> _pendingIntents = new(StringComparer.Ordinal);
    double _nextGuestObserve;
    bool _haveBaseline;   // guest: a snapshot was applied
    double _nextObserve, _nextDigest, _lastSnapshotAsk = -100;

    public WorldSync(Action<string> log, Action<byte, bool, byte[]> send)
    {
        _log = log;
        _send = send;
    }

    public const byte Channel = 7; // GameMsg value used for every world message
    public double ObserveEvery = 0.25;
    public double DigestEvery = 5;

    /// <summary>True while a remote change is being applied, so hooks treat writes as ours, not as intents.</summary>
    public bool Applying { get; private set; }
    public uint Seq => _seq;
    public bool HasBaseline => _haveBaseline;
    public long TxnsCommitted { get; private set; }
    public long TxnsApplied { get; private set; }
    public long IntentsSent { get; private set; }
    public long IntentsApplied { get; private set; }
    public long Repairs { get; private set; }
    public long SnapshotsApplied { get; private set; }
    public string LastRepair { get; private set; } = "";
    public WorldState Committed => _committed;

    public void Reset()
    {
        _hostLog.Clear(); _pending.Clear(); _committed = new(); _seq = 0; _haveBaseline = false; _pendingIntents.Clear();
        _nextObserve = _nextDigest = 0;
    }

    // ---------------- host ----------------

    /// <summary>Host: read the world and commit what changed since the last commit.</summary>
    public void HostTick(IWorldPort port, double now)
    {
        if (now < _nextObserve) return;
        _nextObserve = now + ObserveEvery;
        var cur = port.Read();
        if (cur == null) return;
        var diff = cur.DiffFrom(_committed);
        if (diff.Count == 0) return;
        _committed = cur;
        _seq++;
        _hostLog.Add((_seq, diff));
        if (_hostLog.Count > 256) _hostLog.RemoveAt(0);
        TxnsCommitted++;
        _send(PeerIds.Everyone, true, EncodeTxn(_seq, diff));
    }

    /// <summary>Host: a new player joined (or asked); give them the whole state.</summary>
    public void HostSendSnapshot(byte to) => _send(to, true, EncodeSnapshot(_seq, _committed));

    public void HostReceive(byte from, ref WireReader r, IWorldPort port)
    {
        switch ((WorldMsg)r.U8())
        {
            case WorldMsg.Intent:
                r.U32(); // intent number (ordered transport: each arrives once)
                var e = WorldEffect.Read(ref r);
                Applying = false;
                port.Apply(e); // through the game's own path: the host's scripts react, and the next HostTick commits it
                IntentsApplied++;
                _log($"intent from player {from}: {e.Key}={(e.Kind == WorldValueKind.Str ? e.Str : e.Int)}");
                _nextObserve = 0; // commit promptly so the requester sees it soon
                break;
            case WorldMsg.Digest:
                uint seq = r.U32();
                ulong hash = r.U64();
                if (seq == _seq && hash != _committed.Hash())
                {
                    Repairs++;
                    LastRepair = $"player {from} at seq {seq}";
                    _log($"digest mismatch from player {from} at seq {seq}: sending a snapshot");
                    HostSendSnapshot(from);
                }
                break;
            case WorldMsg.SnapshotRequest:
                HostSendSnapshot(from);
                break;
        }
    }

    // ---------------- guest ----------------

    /// <summary>Guest: one of our scripts wanted to change shared state. Ask the host instead of changing it.</summary>
    public void GuestIntent(in WorldEffect e)
    {
        IntentsSent++;
        var w = new WireWriter().U8(Channel).U8((byte)WorldMsg.Intent).U32(++_intentSeq);
        e.Write(w);
        _send(PeerIds.Host, true, w.ToArray());
    }

    public void GuestReceive(ref WireReader r, IWorldPort port, double now)
    {
        switch ((WorldMsg)r.U8())
        {
            case WorldMsg.Txn:
                uint seq = r.U32();
                var fx = ReadEffects(ref r);
                if (!_haveBaseline || seq != _seq + 1)
                {
                    if (seq > _seq) _pending[seq] = fx;
                    if (!_haveBaseline || seq > _seq + 1) AskSnapshot(now);
                    return;
                }
                ApplyTxn(seq, fx, port);
                DrainPending(port);
                break;
            case WorldMsg.Snapshot:
                uint sseq = r.U32();
                int n = (int)r.U32();
                var snap = new WorldState();
                for (int i = 0; i < n; i++) snap.Set(WorldEffect.Read(ref r));
                ApplySnapshot(sseq, snap, port);
                break;
        }
    }

    void AskSnapshot(double now)
    {
        if (now - _lastSnapshotAsk < 2) return;
        _lastSnapshotAsk = now;
        _send(PeerIds.Host, true, [Channel, (byte)WorldMsg.SnapshotRequest]);
    }

    void ApplyTxn(uint seq, List<WorldEffect> fx, IWorldPort port)
    {
        Applying = true;
        try { foreach (var e in fx) { port.Apply(e); _committed.Set(e); _pendingIntents.Remove(e.Key); } }
        finally { Applying = false; }
        _seq = seq;
        TxnsApplied++;
    }

    void DrainPending(IWorldPort port)
    {
        while (_pending.Count > 0)
        {
            var first = _pending.First();
            if (first.Key <= _seq) { _pending.Remove(first.Key); continue; }
            if (first.Key != _seq + 1) break;
            _pending.Remove(first.Key);
            ApplyTxn(first.Key, first.Value, port);
        }
    }

    /// <summary>Guest: our world was replaced (a level load). Ask for the host's state again, and report no
    /// progress of our own until it is applied: a fresh level's default flags are not something we did.</summary>
    public void Resync()
    {
        _haveBaseline = false;
        _pendingIntents.Clear();
        _lastSnapshotAsk = -100;
    }

    void ApplySnapshot(uint seq, WorldState snap, IWorldPort port)
    {
        var local = port.Read();
        if (local == null)
        {
            // Still loading: apply when there is a world to apply to (GuestTick asks again).
            _lastSnapshotAsk = -100;
            return;
        }
        // Force-set everything the host has, and clear flags we have that the host does not.
        var fx = snap.DiffFrom(local);
        Applying = true;
        try { foreach (var e in fx) port.Apply(e); }
        finally { Applying = false; }
        _committed = snap.Clone();
        _seq = seq;
        _haveBaseline = true;
        SnapshotsApplied++;
        _log($"snapshot {seq}: {snap.CountSet} values, {fx.Count} changed locally");
        DrainPending(port);
    }

    /// <summary>Is a local value a step forward from the host's (a flag turned on, a chapter raised)? Those are the
    /// guest's own progress and go to the host as intents. Steps back are never sent: the digest repairs them.
    /// (coop-anything effect-capture merge rules: flags OR, stage numbers maximum wins, text host wins.)</summary>
    static bool IsProgress(in WorldEffect local, WorldState committed)
    {
        if (local.Key.StartsWith("lvl:", StringComparison.Ordinal) || local.Key.StartsWith("obj:", StringComparison.Ordinal)) return false;
        committed.Values.TryGetValue(local.Key, out var host);
        return local.Kind switch
        {
            WorldValueKind.Flag => local.Int != 0 && (host.Key == null || host.Int == 0),
            WorldValueKind.Int => host.Key == null || local.Int > host.Int,
            _ => false,
        };
    }

    /// <summary>Guest: report our own progress as intents, and periodically prove we match the host so it can
    /// repair anything else that drifted.</summary>
    public void GuestTick(IWorldPort port, double now)
    {
        if (!_haveBaseline)
        {
            if (port.Read() != null) AskSnapshot(now);
            return;
        }
        if (now >= _nextGuestObserve)
        {
            _nextGuestObserve = now + ObserveEvery;
            var local = port.Read();
            if (local != null)
            {
                foreach (var e in local.DiffFrom(_committed))
                {
                    if (!IsProgress(e, _committed)) continue;
                    if (_pendingIntents.TryGetValue(e.Key, out var p) && p.e == e && now - p.at < 5) continue; // already asked
                    _pendingIntents[e.Key] = (e, now);
                    GuestIntent(e);
                }
            }
        }
        // While our own progress is on its way to the host, a digest would only report it as drift.
        if (_pendingIntents.Count > 0 && _pendingIntents.Values.Any(p => now - p.at < 5)) return;
        if (now < _nextDigest || Mutants.On("no_digest")) return;
        _nextDigest = now + DigestEvery;
        var cur = port.Read();
        if (cur == null) return;
        _send(PeerIds.Host, true, new WireWriter().U8(Channel).U8((byte)WorldMsg.Digest).U32(_seq).U64(cur.HashAgainst(_committed)).ToArray());
    }

    // ---------------- encoding ----------------

    static byte[] EncodeTxn(uint seq, List<WorldEffect> fx)
    {
        var w = new WireWriter().U8(Channel).U8((byte)WorldMsg.Txn).U32(seq).U32((uint)fx.Count);
        foreach (var e in fx) e.Write(w);
        return w.ToArray();
    }

    static byte[] EncodeSnapshot(uint seq, WorldState s)
    {
        var w = new WireWriter().U8(Channel).U8((byte)WorldMsg.Snapshot).U32(seq).U32((uint)s.Values.Count);
        foreach (var e in s.Values.Values) e.Write(w);
        return w.ToArray();
    }

    static List<WorldEffect> ReadEffects(ref WireReader r)
    {
        int n = (int)r.U32();
        if (n > 100000) throw new WireException("too many effects");
        var fx = new List<WorldEffect>(n);
        for (int i = 0; i < n; i++) fx.Add(WorldEffect.Read(ref r));
        return fx;
    }
}
