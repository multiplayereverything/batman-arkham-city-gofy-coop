#nullable enable
// PVP: players fight each other the way they fight thugs. On every machine each other player also has a hidden
// thug "combat proxy" standing in their puppet, so freeflow combat targets them and the counter icon shows over
// them. The defender's machine decides every hit on the defender, because only it knows whether the defender
// countered in time:
//   1. A strikes at B's proxy on A's machine -> Attack(A->B).
//   2. B's machine makes A's proxy (on B's machine) attack B with a real thug attack: the counter icon shows, and
//      the game itself either lands the hit (B loses health) or B counters.
//   3. B's machine tells everyone the outcome: Hit, or Countered (then A's machine applies the counter to A).
//   4. A player brought down by another player is Defeated: back up with full health a few seconds later, no
//      game over.
using System;
using System.Collections.Generic;

namespace BatCoop.Core;

public enum PvpMsg : byte
{
    Attack = 1,    // attacker -> defender: u32 attack id, str move class
    Outcome = 2,   // defender -> everyone: u32 attack id, u8 attacker, u8 defender, u8 PvpOutcome, u16 damage
    Defeated = 3,  // victim -> everyone: u8 victim, u8 by
}

public enum PvpOutcome : byte { Hit = 1, Countered = 2, Missed = 3 }

public readonly record struct PvpAttack(uint Id, byte Attacker, byte Defender, string Move);
public readonly record struct PvpResult(uint Id, byte Attacker, byte Defender, PvpOutcome Outcome, ushort Damage);

public static class PvpMessages
{
    public static byte[] Attack(uint id, string move) =>
        new WireWriter().U8((byte)GameMsg.Pvp).U8((byte)PvpMsg.Attack).U32(id).Str(move).ToArray();

    public static byte[] Outcome(in PvpResult r) =>
        new WireWriter().U8((byte)GameMsg.Pvp).U8((byte)PvpMsg.Outcome).U32(r.Id).U8(r.Attacker).U8(r.Defender).U8((byte)r.Outcome).U16(r.Damage).ToArray();

    public static byte[] Defeated(byte victim, byte by) =>
        new WireWriter().U8((byte)GameMsg.Pvp).U8((byte)PvpMsg.Defeated).U8(victim).U8(by).ToArray();
}

/// <summary>Game-agnostic PVP bookkeeping for one machine: which incoming attacks wait to be played, which of our
/// attacks wait for an outcome, and who is down. The game layer plays attacks and reports what happened.</summary>
public sealed class PvpState
{
    readonly Action<byte[], byte> _send; // payload, to
    uint _attackSeq;
    readonly Dictionary<uint, (PvpAttack a, double at)> _outgoing = [];
    readonly Queue<PvpAttack> _incoming = new();
    readonly Dictionary<byte, double> _downUntil = [];

    public bool Enabled;
    /// <summary>How long a defender's machine has to resolve an attack before the attacker forgets it.</summary>
    public double OutcomeTimeout = 4;
    /// <summary>How long a defeated player stays down (immune to PVP hits) before getting up with full health.</summary>
    public double DownSeconds = 5;

    public long AttacksSent, AttacksReceived, Hits, Counters, Misses, Defeats;
    public event Action<PvpResult>? OutcomeReceived;
    public event Action<byte, byte>? DefeatedReceived; // victim, by

    public PvpState(Action<byte[], byte> send) => _send = send;

    public void Reset()
    {
        _outgoing.Clear();
        _incoming.Clear();
        _downUntil.Clear();
    }

    /// <summary>Attacker side: our strike at <paramref name="defender"/>'s proxy started.</summary>
    public uint SendAttack(byte self, byte defender, string move, double now)
    {
        if (!Enabled || IsDown(defender, now) || IsDown(self, now)) return 0;
        var id = ++_attackSeq;
        _outgoing[id] = (new PvpAttack(id, self, defender, move), now);
        AttacksSent++;
        _send(PvpMessages.Attack(id, move), defender);
        return id;
    }

    /// <summary>Defender side: attacks to play, oldest first.</summary>
    public bool TryTakeIncoming(out PvpAttack a) => _incoming.TryDequeue(out a);

    /// <summary>Defender side: what happened to an attack on us; everyone hears it.</summary>
    public void Report(in PvpResult r)
    {
        Count(r);
        _send(PvpMessages.Outcome(r), PeerIds.Everyone);
        OutcomeReceived?.Invoke(r); // our own feed too
    }

    /// <summary>Victim side: a player brought us down.</summary>
    public void ReportDefeated(byte self, byte by, double now)
    {
        _downUntil[self] = now + DownSeconds;
        Defeats++;
        _send(PvpMessages.Defeated(self, by), PeerIds.Everyone);
        DefeatedReceived?.Invoke(self, by);
    }

    public bool IsDown(byte player, double now) => _downUntil.TryGetValue(player, out var t) && now < t;

    /// <summary>Attacker side: attacks the defender never answered (left, changed map) are dropped.</summary>
    public void Tick(double now)
    {
        foreach (var id in new List<uint>(_outgoing.Keys))
            if (now - _outgoing[id].at > OutcomeTimeout) _outgoing.Remove(id);
    }

    public int Pending => _outgoing.Count;

    void Count(in PvpResult r)
    {
        switch (r.Outcome)
        {
            case PvpOutcome.Hit: Hits++; break;
            case PvpOutcome.Countered: Counters++; break;
            default: Misses++; break;
        }
    }

    public void Receive(byte from, ref WireReader r, double now)
    {
        switch ((PvpMsg)r.U8())
        {
            case PvpMsg.Attack:
                var id = r.U32();
                var move = r.Str();
                if (!Enabled) return;
                AttacksReceived++;
                _incoming.Enqueue(new PvpAttack(id, from, PeerIds.None, move));
                while (_incoming.Count > 4) _incoming.Dequeue(); // a flurry: keep the newest few
                break;
            case PvpMsg.Outcome:
                var res = new PvpResult(r.U32(), r.U8(), r.U8(), (PvpOutcome)r.U8(), r.U16());
                _outgoing.Remove(res.Id);
                Count(res);
                OutcomeReceived?.Invoke(res);
                break;
            case PvpMsg.Defeated:
                byte victim = r.U8(), by = r.U8();
                _downUntil[victim] = now + DownSeconds;
                Defeats++;
                DefeatedReceived?.Invoke(victim, by);
                break;
        }
    }
}
