#nullable enable
// Cutscene vote skip, counted by the host: pressing skip in a co-op game is a vote; the cutscene is skipped on every
// machine at once when every player who is watching it has voted.
using System;
using System.Collections.Generic;
using System.Linq;

namespace BatCoop.Core;

public enum VoteMsg : byte { Watching = 1, Vote = 2, Tally = 3, Skip = 4 }

public sealed class CutsceneVote
{
    public const byte Channel = 9;
    readonly Action<byte, bool, byte[]> _send;
    readonly Dictionary<byte, bool> _watching = [];   // host: who is in a skippable cutscene
    readonly HashSet<byte> _votes = [];               // host: who voted in the current round
    uint _round = 1;
    uint _lastSkipDone;
    bool _localWatching;

    public CutsceneVote(Action<byte, bool, byte[]> send) => _send = send;

    /// <summary>Latest tally for the HUD (host computes it, guests receive it).</summary>
    public int Votes { get; private set; }
    public int Needed { get; private set; }
    public bool Active => Needed > 0;
    public bool LocalVoted { get; private set; }
    public long SkipsDone { get; private set; }

    /// <summary>Raised on every machine when the vote passes; the game layer then skips its cutscene once.</summary>
    public event Action? SkipNow;

    public void Reset()
    {
        // A new room has a new host whose skip rounds start again at 1.
        _watching.Clear(); _votes.Clear(); Votes = Needed = 0; LocalVoted = false; _localWatching = false;
        _round = 1; _lastSkipDone = 0;
    }

    // ---- local player (every machine) ----

    public void LocalWatching(bool watching, bool isHost, byte localId)
    {
        if (watching == _localWatching) return;
        _localWatching = watching;
        if (!watching) LocalVoted = false;
        if (isHost) HostWatching(localId, watching);
        else _send(PeerIds.Host, true, [Channel, (byte)VoteMsg.Watching, watching ? (byte)1 : (byte)0]);
    }

    public void LocalVote(bool isHost, byte localId)
    {
        if (!_localWatching || LocalVoted) return;
        LocalVoted = true;
        if (isHost) HostVote(localId);
        else _send(PeerIds.Host, true, [Channel, (byte)VoteMsg.Vote]);
    }

    // ---- host ----

    public void HostWatching(byte player, bool watching)
    {
        _watching[player] = watching;
        if (!watching) _votes.Remove(player);
        HostEvaluate();
    }

    public void HostVote(byte player)
    {
        if (!_watching.GetValueOrDefault(player)) return;
        _votes.Add(player);
        HostEvaluate();
    }

    public void HostPlayerLeft(byte player)
    {
        _watching.Remove(player);
        _votes.Remove(player);
        HostEvaluate();
    }

    void HostEvaluate()
    {
        var watchers = _watching.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        int needed = watchers.Count;
        int votes = watchers.Count(_votes.Contains);
        if (needed == 0) { _votes.Clear(); }
        if (needed > 0 && votes >= needed)
        {
            uint round = _round++;
            _votes.Clear();
            _send(PeerIds.Everyone, true, [Channel, (byte)VoteMsg.Skip, .. BitConverter.GetBytes(round)]);
            DoSkip(round);
            votes = 0;
        }
        if (votes != Votes || needed != Needed)
        {
            Votes = votes; Needed = needed;
            _send(PeerIds.Everyone, true, [Channel, (byte)VoteMsg.Tally, (byte)votes, (byte)needed]);
        }
    }

    public void HostReceive(byte from, ref WireReader r)
    {
        switch ((VoteMsg)r.U8())
        {
            case VoteMsg.Watching: HostWatching(from, r.U8() != 0); break;
            case VoteMsg.Vote: HostVote(from); break;
        }
    }

    // ---- guest ----

    public void GuestReceive(ref WireReader r)
    {
        switch ((VoteMsg)r.U8())
        {
            case VoteMsg.Tally: Votes = r.U8(); Needed = r.U8(); break;
            case VoteMsg.Skip: DoSkip(r.U32()); break;
        }
    }

    void DoSkip(uint round)
    {
        if (round <= _lastSkipDone && !Mutants.On("skip_twice")) return; // a repeated skip message never skips a later cutscene
        _lastSkipDone = round;
        LocalVoted = false;
        SkipsDone++;
        SkipNow?.Invoke();
    }
}
