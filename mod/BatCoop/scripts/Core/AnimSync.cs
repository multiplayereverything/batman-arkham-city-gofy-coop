#nullable enable
// Animation sync. Arkham City animates every character through one pose player: a pose description (movement
// and weapon stance, an optional transition with its clip, target and timing) plus a move-to target and a
// movement speed. The pose calls themselves are native (no redirect sees them), so the owner samples its own
// pawn instead: every new transition (the pose player's transition id goes up) is sent once, reliably, and the
// puppet on every other machine starts the same transition. Locomotion is the puppet's own: it is walked to
// the interpolated position at the owner's movement speed, so it plays the game's walk, run and sprint cycles.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace BatCoop.Core;

/// <summary>The game's EAnimTransitionType values.</summary>
public enum PoseTransition : byte { Normal = 0, Absolute = 1, AbsoluteRotationOnly = 2, Relative = 3, RelativeRotationOnly = 4, Slaved = 5 }

/// <summary>One transition of a player's pose. Names are the game's FName strings ("" for None); clips are
/// AnimSequence path names; positions are world coordinates and yaws degrees, the same on every machine.</summary>
public readonly record struct PoseCall(
    PoseTransition Type, string Movement, string Weapon, string Idle, string Transition,
    string Clip, string ClipName, string UpperClip, string IdleClip,
    bool Mirrored, byte MirrorChoice, float DurationScale, bool OverrideDuration, float Duration,
    Vector3 TargetPos, float TargetYaw, float MeetingBegin, float StartTime)
{
    public byte[] Encode(uint seq) => new WireWriter()
        .U8((byte)GameMsg.Anim).U32(seq).U8((byte)Type)
        .Str(Movement).Str(Weapon).Str(Idle).Str(Transition)
        .Str(Clip).Str(ClipName).Str(UpperClip).Str(IdleClip)
        .Bool(Mirrored).U8(MirrorChoice).F32(DurationScale).Bool(OverrideDuration).F32(Duration)
        .F32(TargetPos.X).F32(TargetPos.Y).F32(TargetPos.Z).F32(TargetYaw).F32(MeetingBegin).F32(StartTime)
        .ToArray();

    public static PoseCall Decode(ref WireReader r, out uint seq)
    {
        seq = r.U32();
        return new PoseCall((PoseTransition)r.U8(), r.Str(), r.Str(), r.Str(), r.Str(),
            r.Str(), r.Str(), r.Str(), r.Str(),
            r.Bool(), r.U8(), r.F32(), r.Bool(), r.F32(),
            new Vector3(r.F32(), r.F32(), r.F32()), r.F32(), r.F32(), r.F32());
    }

    /// <summary>The short name of the clip ("BM_Attack:AnimSequence_19"), for logs and the lab.</summary>
    public string ClipShort => Clip.Length > 0 ? Clip[(Clip.LastIndexOf('.') + 1)..] : ClipName;

    public override string ToString() =>
        $"{Type} {Movement}/{Weapon}{(Idle.Length > 0 ? "/" + Idle : "")}" +
        (Transition.Length > 0 ? $" '{Transition}'" : "") + (ClipShort.Length > 0 ? $" clip {ClipShort}" : "") +
        (Type is PoseTransition.Relative or PoseTransition.Slaved ? $" at {TargetPos.X:0},{TargetPos.Y:0},{TargetPos.Z:0}" : "") +
        (Type is PoseTransition.Relative or PoseTransition.RelativeRotationOnly or PoseTransition.Slaved ? $" yaw {TargetYaw:0}" : "") +
        (Mirrored ? " mirrored" : "");
}

/// <summary>Per remote player: transitions waiting to be replayed. Each waits for the interpolation delay, so the
/// animation starts when the puppet's (delayed) position reaches the place it started on the owner.</summary>
public sealed class PoseQueue
{
    readonly Queue<(double at, PoseCall call)> _q = new();
    uint _lastSeq;
    public long Received, Played;
    public PoseCall? Last;

    public void Add(uint seq, PoseCall call, double now)
    {
        // Reliable and ordered, but a resent snapshot after a world change can repeat a sequence number.
        if (seq != 0 && seq <= _lastSeq) return;
        _lastSeq = seq;
        Received++;
        _q.Enqueue((now, call));
        while (_q.Count > 32) _q.Dequeue();
    }

    public bool TryTake(double now, double delay, out PoseCall call)
    {
        if (_q.Count > 0 && now - _q.Peek().at >= delay)
        {
            call = _q.Dequeue().call;
            Played++;
            Last = call;
            return true;
        }
        call = default;
        return false;
    }

    public void Clear() { _q.Clear(); _lastSeq = 0; }
}

/// <summary>Owner side: turns sampled pose descriptions into PoseCalls, once per new transition.</summary>
public sealed class PoseSender
{
    int _lastTransitionId = int.MinValue;
    uint _seq;
    double _nextRefresh;
    PoseCall? _current;
    public long Sent;
    public PoseCall? Last => _current;

    /// <summary>Call every frame with the pose player's transition id; <paramref name="read"/> is called only
    /// when a new transition started. Every few seconds the current stance is sent again (a newcomer, or a puppet
    /// that respawned, picks it up).</summary>
    public byte[]? Tick(int transitionId, Func<PoseCall> read, double now)
    {
        if (transitionId != _lastTransitionId)
        {
            _lastTransitionId = transitionId;
            _current = read();
            _nextRefresh = now + 3;
            Sent++;
            return _current.Value.Encode(++_seq);
        }
        if (_current is { } c && now >= _nextRefresh)
        {
            _nextRefresh = now + 3;
            // The refresh carries the stance only: replaying the last strike again would be wrong.
            var stance = c with { Type = PoseTransition.Normal, Transition = "", Clip = "", ClipName = "", UpperClip = "" };
            return stance.Encode(++_seq);
        }
        return null;
    }

    public void Reset() { _lastTransitionId = int.MinValue; _current = null; }
}
