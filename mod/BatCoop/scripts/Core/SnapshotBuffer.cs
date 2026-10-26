#nullable enable
// Snapshot interpolation for one remote player: render slightly in the past and blend between received states.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace BatCoop.Core;

public readonly record struct RenderSample(Vector3 Pos, float Yaw, float Pitch, Vector3 Vel, PlayerState Latest, bool Snapped);

public sealed class SnapshotBuffer
{
    public double Delay = 0.10;          // seconds rendered behind the newest data
    public double MaxExtrapolate = 0.25; // seconds of dead reckoning when data stops
    public float SnapDistance = 1500f;   // a jump larger than this between states is a teleport, not movement

    readonly List<PlayerState> _states = [];
    double _offset;                      // local clock minus sender clock, tracks the fastest delivery seen
    bool _hasOffset;
    uint _newestSeq;

    public int Count => _states.Count;
    public bool HasData => _states.Count > 0;
    public PlayerState Newest => _states[^1];

    /// <summary>Adds a state received at local time <paramref name="localNow"/>. Returns false for stale or duplicate states.</summary>
    public bool Add(in PlayerState s, double localNow)
    {
        if (_states.Count > 0 && SeqNotNewer(s.Seq, _newestSeq))
        {
            // Out of order. Keep it only if it still falls inside the window we render from.
            uint seq = s.Seq;
            if (_states.Exists(x => x.Seq == seq)) return false;
            if (s.Time < _states[0].Time) return false;
        }

        double off = localNow - s.Time;
        if (!_hasOffset || off < _offset) { _offset = off; _hasOffset = true; }
        else _offset += (off - _offset) * 0.002; // follow clock drift and route changes slowly

        int i = _states.Count;
        while (i > 0 && _states[i - 1].Time > s.Time) i--;
        _states.Insert(i, s);
        if (!SeqNotNewer(s.Seq, _newestSeq) || _states.Count == 1) _newestSeq = s.Seq;

        if (_states.Count > 64) _states.RemoveRange(0, _states.Count - 64);
        return true;
    }

    static bool SeqNotNewer(uint a, uint b) => (int)(a - b) <= 0;

    public void Clear() { _states.Clear(); _hasOffset = false; }

    public bool Sample(double localNow, out RenderSample sample)
    {
        sample = default;
        if (_states.Count == 0) return false;

        double t = localNow - _offset - Delay;
        var newest = _states[^1];

        // Drop states we can no longer need (keep one before t to interpolate from).
        while (_states.Count > 2 && _states[1].Time <= t) _states.RemoveAt(0);

        var a = _states[0];
        if (t <= a.Time || _states.Count == 1)
        {
            if (_states.Count == 1 && t > a.Time) sample = Extrapolate(a, t - a.Time, newest);
            else sample = new RenderSample(a.Pos, a.Yaw, a.Pitch, a.Vel, newest, false);
            return true;
        }

        var b = _states[1];
        if (t >= b.Time)
        {
            // Past the newest pair: only b remains relevant.
            sample = Extrapolate(b, t - b.Time, newest);
            return true;
        }

        if (Vector3.Distance(a.Pos, b.Pos) > SnapDistance)
        {
            sample = new RenderSample(b.Pos, b.Yaw, b.Pitch, b.Vel, newest, true);
            return true;
        }

        float f = (float)((t - a.Time) / Math.Max(1e-6, b.Time - a.Time));
        sample = new RenderSample(
            Vector3.Lerp(a.Pos, b.Pos, f),
            LerpAngle(a.Yaw, b.Yaw, f),
            LerpAngle(a.Pitch, b.Pitch, f),
            Vector3.Lerp(a.Vel, b.Vel, f),
            newest,
            false);
        return true;
    }

    RenderSample Extrapolate(in PlayerState s, double dt, in PlayerState newest)
    {
        double d = Math.Clamp(dt, 0, MaxExtrapolate);
        // A player standing still or on the ground with no speed should not drift.
        var pos = s.Pos + s.Vel * (float)d;
        return new RenderSample(pos, s.Yaw, s.Pitch, s.Vel, newest, false);
    }

    public static float LerpAngle(float a, float b, float f)
    {
        float d = ((b - a) % 360f + 540f) % 360f - 180f;
        return Normalize(a + d * f);
    }

    public static float Normalize(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg <= -180f) deg += 360f;
        return deg;
    }
}
