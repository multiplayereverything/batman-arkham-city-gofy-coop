#nullable enable
// NPCs are owned by the host (coop-anything playbook/actor-ownership.md, "one host runs all AI"): the host streams
// every NPC near any player; guests show those NPCs as remote-driven copies and keep their own AI frozen.
// Results are copied, not events: a knocked-out NPC is sent as state, and a guest's hit is an intent the host applies.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace BatCoop.Core;

[Flags]
public enum NpcFlags : byte { None = 0, Down = 1, Hidden = 2, InCombat = 4 }

/// <summary>How an NPC is identified and rebuilt on another machine.</summary>
/// <param name="Key">The pawn's object path on the host (level-placed NPCs have the same path everywhere).</param>
public sealed record NpcInfo(uint Id, string Key, string PawnClass, string CharacterClass);

public struct NpcState
{
    public uint Id;
    public Vector3 Pos;
    public float Yaw;
    public Vector3 Vel;
    public byte Physics;
    public NpcFlags Flags;
    public ushort Health;
}

/// <summary>One NPC as the host's game sees it.</summary>
/// <param name="Handle">Stable for the actor's lifetime (the engine object address).</param>
public readonly record struct NpcSample(long Handle, string Key, string PawnClass, string CharacterClass, NpcState State);

public interface INpcHostPort
{
    /// <summary>All live NPCs the host could share (the session filters by distance).</summary>
    IEnumerable<NpcSample> Sample();
    /// <summary>Apply a guest's hit to the host NPC behind this handle.</summary>
    void ApplyHit(long handle, in NpcHit hit);
}

public readonly record struct NpcHit(uint NpcId, int Damage, Vector3 HitLocation, Vector3 Momentum, string DamageType, byte Attacker);

public enum NpcMsg : byte { Info = 1, Gone = 2, States = 3, Hit = 4, Epoch = 5 }

/// <summary>Host side: decides which NPCs are shared, numbers them, streams them.</summary>
public sealed class NpcHost
{
    public const byte Channel = 8;
    readonly Action<byte, bool, byte[]> _send;
    readonly Dictionary<long, uint> _ids = [];
    readonly Dictionary<uint, (long handle, NpcInfo info)> _shared = [];
    uint _nextId = 1;
    double _nextSend;

    public NpcHost(Action<byte, bool, byte[]> send) => _send = send;

    public float InterestRadius = 6000;   // 60 m around any player
    public double SendEvery = 0.1;        // 10 Hz
    public int MaxPerPacket = 48;
    public uint Epoch { get; private set; } = 1;
    public int SharedCount => _shared.Count;
    public long HitsApplied { get; private set; }

    public void Reset()
    {
        _ids.Clear();
        _shared.Clear();
        Epoch++;
        _send(PeerIds.Everyone, true, new WireWriter().U8(Channel).U8((byte)NpcMsg.Epoch).U32(Epoch).ToArray());
    }

    /// <summary>A new guest needs every shared NPC's description.</summary>
    public void SendAllInfo(byte to)
    {
        _send(to, true, new WireWriter().U8(Channel).U8((byte)NpcMsg.Epoch).U32(Epoch).ToArray());
        foreach (var (_, (_, info)) in _shared) _send(to, true, EncodeInfo(Epoch, info));
    }

    public void Tick(INpcHostPort port, IReadOnlyList<Vector3> players, double now)
    {
        if (now < _nextSend) return;
        _nextSend = now + SendEvery;

        var seen = new HashSet<uint>();
        var states = new List<NpcState>();
        float r2 = InterestRadius * InterestRadius;
        foreach (var s in port.Sample())
        {
            bool near = false;
            foreach (var p in players) if (Vector3.DistanceSquared(p, s.State.Pos) <= r2) { near = true; break; }
            if (!near) continue;
            // An address can be reused by a new actor after the old one is destroyed: a different key is a new NPC.
            if (_ids.TryGetValue(s.Handle, out var known) && _shared.TryGetValue(known, out var was) && was.info.Key != s.Key)
                _ids.Remove(s.Handle);
            if (!_ids.TryGetValue(s.Handle, out var id))
            {
                id = _nextId++;
                _ids[s.Handle] = id;
            }
            if (!_shared.ContainsKey(id))
            {
                var info = new NpcInfo(id, s.Key, s.PawnClass, s.CharacterClass);
                _shared[id] = (s.Handle, info);
                _send(PeerIds.Everyone, true, EncodeInfo(Epoch, info));
            }
            seen.Add(id);
            var st = s.State;
            st.Id = id;
            states.Add(st);
        }
        foreach (var id in new List<uint>(_shared.Keys))
        {
            if (seen.Contains(id)) continue;
            _ids.Remove(_shared[id].handle);
            _shared.Remove(id);
            _send(PeerIds.Everyone, true, new WireWriter().U8(Channel).U8((byte)NpcMsg.Gone).U32(Epoch).U32(id).ToArray());
        }
        for (int i = 0; i < states.Count; i += MaxPerPacket)
        {
            var w = new WireWriter().U8(Channel).U8((byte)NpcMsg.States).U32(Epoch).F32((float)now);
            int n = Math.Min(MaxPerPacket, states.Count - i);
            w.U16((ushort)n);
            for (int j = i; j < i + n; j++) WriteState(w, states[j]);
            _send(PeerIds.Everyone, false, w.ToArray());
        }
    }

    public void Receive(byte from, ref WireReader r, INpcHostPort port)
    {
        if ((NpcMsg)r.U8() != NpcMsg.Hit) return;
        uint epoch = r.U32();
        var hit = ReadHit(ref r) with { Attacker = from };
        if ((epoch != Epoch && !Mutants.On("no_epoch")) || !_shared.TryGetValue(hit.NpcId, out var e)) return; // stale: the NPC is gone or renumbered
        port.ApplyHit(e.handle, hit);
        HitsApplied++;
    }

    static byte[] EncodeInfo(uint epoch, NpcInfo i) =>
        new WireWriter().U8(Channel).U8((byte)NpcMsg.Info).U32(epoch).U32(i.Id).Str(i.Key).Str(i.PawnClass).Str(i.CharacterClass).ToArray();

    internal static void WriteState(WireWriter w, in NpcState s) =>
        w.U32(s.Id).F32(s.Pos.X).F32(s.Pos.Y).F32(s.Pos.Z).F32(s.Yaw).F32(s.Vel.X).F32(s.Vel.Y).F32(s.Vel.Z)
         .U8(s.Physics).U8((byte)s.Flags).U16(s.Health);

    internal static NpcState ReadState(ref WireReader r) => new()
    {
        Id = r.U32(),
        Pos = new Vector3(r.F32(), r.F32(), r.F32()),
        Yaw = r.F32(),
        Vel = new Vector3(r.F32(), r.F32(), r.F32()),
        Physics = r.U8(),
        Flags = (NpcFlags)r.U8(),
        Health = r.U16(),
    };

    internal static NpcHit ReadHit(ref WireReader r) =>
        new(r.U32(), (int)r.U32(), new Vector3(r.F32(), r.F32(), r.F32()), new Vector3(r.F32(), r.F32(), r.F32()), r.Str(), 0);
}

/// <summary>One mirrored NPC on a guest.</summary>
public sealed class NpcMirror(NpcInfo info)
{
    public NpcInfo Info { get; } = info;
    public SnapshotBuffer Buffer { get; } = new() { Delay = 0.15 };
    public NpcState Latest;
    public long States;
    /// <summary>The game layer's local actor for this NPC (matched or spawned), or null.</summary>
    public object? Local;
    public bool Spawned;     // true if the game layer created it (destroy on Gone); false if it took over a level NPC
}

/// <summary>Guest side: keeps the host's NPC list and their interpolation buffers. The game layer binds local actors.</summary>
public sealed class NpcGuest
{
    readonly Action<byte, bool, byte[]> _send;
    readonly Dictionary<uint, NpcMirror> _mirrors = [];
    uint _epoch;

    public NpcGuest(Action<byte, bool, byte[]> send) => _send = send;

    public IReadOnlyDictionary<uint, NpcMirror> Mirrors => _mirrors;
    public uint Epoch => _epoch;
    public long HitsSent { get; private set; }
    /// <summary>Mirrors removed since the game layer last looked: it must destroy or release their local actors.</summary>
    public List<NpcMirror> Removed { get; } = [];

    public void Clear()
    {
        Removed.AddRange(_mirrors.Values);
        _mirrors.Clear();
    }

    public void Receive(ref WireReader r, double now)
    {
        var kind = (NpcMsg)r.U8();
        uint epoch = r.U32();
        if (kind == NpcMsg.Epoch)
        {
            if (epoch != _epoch) { Clear(); _epoch = epoch; }
            return;
        }
        if (epoch != _epoch)
        {
            // A message from another ownership epoch is stale: never let it create or move anything.
            if (epoch < _epoch) return;
            Clear();
            _epoch = epoch;
        }
        switch (kind)
        {
            case NpcMsg.Info:
                var info = new NpcInfo(r.U32(), r.Str(), r.Str(), r.Str());
                if (!_mirrors.ContainsKey(info.Id)) _mirrors[info.Id] = new NpcMirror(info);
                break;
            case NpcMsg.Gone:
                if (_mirrors.Remove(r.U32(), out var gone)) Removed.Add(gone);
                break;
            case NpcMsg.States:
                double t = r.F32();
                int n = r.U16();
                for (int i = 0; i < n; i++)
                {
                    var s = NpcHost.ReadState(ref r);
                    if (!_mirrors.TryGetValue(s.Id, out var m)) continue; // Info not here yet: next packet
                    m.Latest = s;
                    m.States++;
                    m.Buffer.Add(new PlayerState
                    {
                        Seq = (uint)m.States, Time = t, Pos = s.Pos, Yaw = s.Yaw, Vel = s.Vel, Physics = s.Physics,
                        Flags = PlayerFlags.InWorld | ((s.Flags & NpcFlags.Hidden) != 0 ? PlayerFlags.Hidden : 0)
                                | ((s.Flags & NpcFlags.Down) != 0 ? PlayerFlags.Dead : 0),
                        Health = s.Health,
                    }, now);
                }
                break;
        }
    }

    /// <summary>The guest hit a mirrored NPC: the host decides what the hit does.</summary>
    public void SendHit(in NpcHit hit)
    {
        HitsSent++;
        _send(PeerIds.Host, true, new WireWriter().U8(NpcHost.Channel).U8((byte)NpcMsg.Hit).U32(_epoch)
            .U32(hit.NpcId).U32((uint)hit.Damage).F32(hit.HitLocation.X).F32(hit.HitLocation.Y).F32(hit.HitLocation.Z)
            .F32(hit.Momentum.X).F32(hit.Momentum.Y).F32(hit.Momentum.Z).Str(hit.DamageType).ToArray());
    }
}
