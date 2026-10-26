using BatCoop.Core;

namespace BatCoop.Link;

/// <summary>Room logic of one link. Star topology: the host's link assigns player ids and forwards every
/// guest's game data to the other guests; guests only ever talk to the host. Runs on one thread.</summary>
public sealed class Hub : IDisposable
{
    sealed class Guest(ulong peer)
    {
        public ulong Peer = peer;
        public byte Id = PeerIds.None; // None until JoinReq is accepted
        public string Name = "";
        public double LastHeard;
        public double ConnectedAt;
    }

    readonly Action<byte[]> _toMod;
    readonly Action<string> _log;
    readonly Func<TransportKind, ITransport> _makeTransport;
    readonly Dictionary<ulong, Guest> _guests = [];   // host side, by transport peer
    readonly Dictionary<byte, string> _names = [];    // guest side: everyone in the room except us
    ITransport? _t;
    ITransport? _steam; // kept between rooms: Steam init is slow and invites need it running
    LinkState _state = LinkState.Idle;
    string _lobby = "";
    string _name = "Player";
    int _maxPlayers = 4;
    byte _myId = PeerIds.None;
    ulong _hostPeer;
    double _hostHeard;
    double _nextKeepAlive;
    double _now;

    public double PeerTimeout { get; set; } = 20;
    public double JoinTimeout { get; set; } = 15;
    public LinkState State => _state;
    public string ModName => _name;

    public Hub(Action<byte[]> toMod, Action<string> log, Func<TransportKind, ITransport> makeTransport)
    {
        _toMod = toMod;
        _log = log;
        _makeTransport = makeTransport;
    }

    void SetState(LinkState s, string text)
    {
        _state = s;
        _log($"state {s}: {text}");
        _toMod(Frames.Status(s, _myId, text, _lobby));
    }

    void ToModNotice(string text) => _toMod(Frames.Notice(text));

    // ---- frames from the local mod ----

    public void OnModFrame(byte[] body)
    {
        var r = new WireReader(body);
        switch ((ModToLink)r.U8())
        {
            case ModToLink.Hello:
                var modVer = r.Str();
                var proto = r.Str();
                _name = Clean(r.Str());
                if (proto != Versions.Protocol)
                {
                    SetState(LinkState.Ended, $"BatCoopLink speaks {Versions.Protocol} but the mod speaks {proto}: reinstall BatCoop");
                    return;
                }
                _log($"mod {modVer} says hello as '{_name}'");
                _toMod(Frames.Status(_state, _myId, _state == LinkState.Idle ? "Ready" : "Reattached", _lobby));
                break;
            case ModToLink.Host:
                var t = (TransportKind)r.U8();
                int max = Math.Clamp((int)r.U8(), 2, 16);
                int port = r.U16();
                bool friendsOnly = r.Bool();
                StartHost(t, max, port, friendsOnly);
                break;
            case ModToLink.Join:
                var jt = (TransportKind)r.U8();
                StartJoin(jt, r.Str());
                break;
            case ModToLink.Leave:
                Leave("left");
                SetState(LinkState.Idle, "Left the game");
                break;
            case ModToLink.Send:
                byte to = r.U8();
                bool reliable = r.Bool();
                var payload = r.Bytes();
                SendFromMod(to, reliable, payload);
                break;
            case ModToLink.Invite:
                _t?.Invite();
                break;
        }
    }

    static string Clean(string s)
    {
        s = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length == 0 ? "Player" : s.Length > 32 ? s[..32] : s;
    }

    void Use(TransportKind kind)
    {
        if (_t != null && _t != _steam) _t.Dispose();
        _t = kind == TransportKind.Steam ? (_steam ??= _makeTransport(kind)) : _makeTransport(kind);
    }

    /// <summary>Starts Steam before any room exists, so invites accepted in the Steam client reach us.</summary>
    public void UseSteamEarly(ITransport steam) => _steam = steam;

    public void StartHost(TransportKind kind, int maxPlayers, int port, bool friendsOnly)
    {
        Leave("host restarted");
        Use(kind);
        _maxPlayers = maxPlayers;
        _myId = PeerIds.Host;
        SetState(LinkState.Connecting, $"Opening a {_t!.Name} room...");
        _t.StartHost(maxPlayers, port, friendsOnly);
    }

    public void StartJoin(TransportKind kind, string target)
    {
        Leave("join restarted");
        Use(kind);
        _myId = PeerIds.None;
        SetState(LinkState.Connecting, target.Length == 0 ? "Looking for a friend's game..." : $"Joining {target}...");
        _t!.StartJoin(target);
    }

    void SendFromMod(byte to, bool reliable, byte[] payload)
    {
        if (_t == null) return;
        if (_state == LinkState.Hosting)
        {
            var env = Data(PeerIds.Host, to, payload);
            foreach (var g in _guests.Values)
                if (g.Id != PeerIds.None && (to == PeerIds.Everyone || to == g.Id)) _t.Send(g.Peer, env, reliable);
        }
        else if (_state == LinkState.Joined)
        {
            _t.Send(_hostPeer, Data(_myId, to, payload), reliable);
        }
    }

    static byte[] Data(byte from, byte to, byte[] payload) =>
        new WireWriter().U8((byte)HubMsg.Data).U8(from).U8(to).Bytes(payload).ToArray();

    // ---- transport events and timers ----

    public void Tick(double now)
    {
        _now = now;
        if (_steam != null && _steam != _t)
        {
            _steam.Poll();
            while (_steam.Events.TryDequeue(out var sev)) if (sev is InviteReceived or TransportNotice) OnTransport(sev);
        }
        if (_t == null) return;
        _t.Poll();
        while (_t != null && _t.Events.TryDequeue(out var ev)) OnTransport(ev);

        if (now >= _nextKeepAlive)
        {
            _nextKeepAlive = now + 1;
            byte[] ka = [(byte)HubMsg.KeepAlive];
            if (_state == LinkState.Hosting)
                foreach (var g in _guests.Values) _t?.Send(g.Peer, ka, true);
            else if (_state == LinkState.Joined || (_state == LinkState.Connecting && _hostPeer != 0))
                _t?.Send(_hostPeer, ka, true);
        }

        if (_state == LinkState.Hosting)
        {
            foreach (var g in _guests.Values.ToArray())
            {
                if (now - g.LastHeard > PeerTimeout) DropGuest(g, "timed out");
                else if (g.Id == PeerIds.None && now - g.ConnectedAt > JoinTimeout) DropGuest(g, "never said hello");
            }
        }
        else if (_state == LinkState.Joined && now - _hostHeard > PeerTimeout)
        {
            Leave("host timed out");
            SetState(LinkState.Ended, "Lost the host (no answer for 20 s)");
        }
        else if (_state == LinkState.Connecting && _hostPeer != 0 && now - _hostHeard > JoinTimeout)
        {
            Leave("no answer");
            SetState(LinkState.Ended, "The host did not answer");
        }
    }

    void OnTransport(TransportEvent ev)
    {
        switch (ev)
        {
            case HostReady h:
                _lobby = h.Lobby;
                SetState(LinkState.Hosting, h.Text);
                break;
            case Connected c:
                _hostPeer = c.HostPeer;
                _hostHeard = _now;
                _t!.Send(_hostPeer, new WireWriter().U8((byte)HubMsg.JoinReq).Str(Versions.Protocol).Str(_name).ToArray(), true);
                _log($"connected to host peer {c.HostPeer}: {c.Text}");
                break;
            case Failed f:
                Leave("failed");
                SetState(LinkState.Ended, f.Reason);
                break;
            case TransportNotice n:
                ToModNotice(n.Text);
                break;
            case InviteReceived inv:
                if (_state is LinkState.Idle or LinkState.Ended)
                {
                    ToModNotice($"Accepted {inv.From}'s invite");
                    StartJoin(TransportKind.Steam, inv.Target);
                }
                else ToModNotice($"{inv.From} invited you; leave this game first (F7), then join with F6");
                break;
            case PeerGone g:
                if (_state == LinkState.Hosting && _guests.TryGetValue(g.Peer, out var guest)) DropGuest(guest, g.Reason);
                else if (_state is LinkState.Joined or LinkState.Connecting && g.Peer == _hostPeer)
                {
                    Leave("host gone");
                    SetState(LinkState.Ended, "The host left (" + g.Reason + ")");
                }
                break;
            case Message m:
                try { OnEnvelope(m.Peer, m.Data); }
                catch (WireException e) { _log($"bad envelope from {m.Peer}: {e.Message}"); }
                break;
        }
    }

    void OnEnvelope(ulong peer, byte[] data)
    {
        var r = new WireReader(data);
        var kind = (HubMsg)r.U8();
        if (_state == LinkState.Hosting) OnHostEnvelope(peer, kind, ref r);
        else if (peer == _hostPeer) OnGuestEnvelope(kind, ref r);
    }

    void OnHostEnvelope(ulong peer, HubMsg kind, ref WireReader r)
    {
        if (!_guests.TryGetValue(peer, out var g))
        {
            g = new Guest(peer) { ConnectedAt = _now };
            _guests[peer] = g;
        }
        g.LastHeard = _now;

        switch (kind)
        {
            case HubMsg.JoinReq:
                var proto = r.Str();
                var name = Clean(r.Str());
                if (g.Id != PeerIds.None) return; // repeated hello
                if (proto != Versions.Protocol) { Deny(g, $"Version mismatch: host runs {Versions.Protocol}, you run {proto}"); return; }
                if (_guests.Values.Count(x => x.Id != PeerIds.None) + 1 >= _maxPlayers) { Deny(g, $"The game is full ({_maxPlayers} players)"); return; }
                g.Id = FreeId();
                g.Name = name;
                var ack = new WireWriter().U8((byte)HubMsg.JoinAck).U8(g.Id).U8((byte)_maxPlayers);
                var others = _guests.Values.Where(x => x.Id != PeerIds.None && x != g).ToList();
                ack.U8((byte)(others.Count + 1)).U8(PeerIds.Host).Str(_name);
                foreach (var o in others) ack.U8(o.Id).Str(o.Name);
                _t!.Send(peer, ack.ToArray(), true);
                var joined = new WireWriter().U8((byte)HubMsg.PeerJoined).U8(g.Id).Str(g.Name).ToArray();
                foreach (var o in others) _t.Send(o.Peer, joined, true);
                _toMod(Frames.PeerJoined(g.Id, g.Name));
                _log($"player {g.Id} '{g.Name}' joined (peer {peer})");
                break;
            case HubMsg.Data:
                if (g.Id == PeerIds.None) return;
                r.U8(); // claimed sender: ignored, the host knows who sent it
                byte to = r.U8();
                var payload = r.Bytes();
                if (to == PeerIds.Host || to == PeerIds.Everyone) _toMod(Frames.Recv(g.Id, payload));
                if (to != PeerIds.Host)
                {
                    var fwd = Data(g.Id, to, payload);
                    foreach (var o in _guests.Values)
                        if (o != g && o.Id != PeerIds.None && (to == PeerIds.Everyone || to == o.Id)) _t!.Send(o.Peer, fwd, payload.Length > 0 && payload[0] != (byte)GameMsgKinds.PlayerState);
                }
                break;
            case HubMsg.Bye:
                DropGuest(g, r.Remaining > 0 ? r.Str() : "left");
                break;
        }
    }

    void Deny(Guest g, string reason)
    {
        _log($"refused peer {g.Peer}: {reason}");
        _t!.Send(g.Peer, new WireWriter().U8((byte)HubMsg.JoinDeny).Str(reason).ToArray(), true);
        _guests.Remove(g.Peer);
    }

    byte FreeId()
    {
        for (byte id = 1; id < 250; id++)
            if (!_guests.Values.Any(g => g.Id == id)) return id;
        throw new InvalidOperationException("no free player id");
    }

    void DropGuest(Guest g, string reason)
    {
        _guests.Remove(g.Peer);
        _t?.Drop(g.Peer);
        if (g.Id == PeerIds.None) return;
        _log($"player {g.Id} '{g.Name}' left: {reason}");
        _toMod(Frames.PeerLeft(g.Id, reason));
        var left = new WireWriter().U8((byte)HubMsg.PeerLeft).U8(g.Id).Str(reason).ToArray();
        foreach (var o in _guests.Values) if (o.Id != PeerIds.None) _t?.Send(o.Peer, left, true);
    }

    void OnGuestEnvelope(HubMsg kind, ref WireReader r)
    {
        _hostHeard = _now;
        switch (kind)
        {
            case HubMsg.JoinAck:
                _myId = r.U8();
                _maxPlayers = r.U8();
                int n = r.U8();
                _names.Clear();
                for (int i = 0; i < n; i++) _names[r.U8()] = r.Str();
                SetState(LinkState.Joined, $"Joined as player {_myId + 1} of {_maxPlayers}");
                foreach (var (id, name) in _names) _toMod(Frames.PeerJoined(id, name));
                break;
            case HubMsg.JoinDeny:
                var reason = r.Str();
                Leave("denied");
                SetState(LinkState.Ended, reason);
                break;
            case HubMsg.PeerJoined:
                var jid = r.U8();
                _names[jid] = r.Str();
                _toMod(Frames.PeerJoined(jid, _names[jid]));
                break;
            case HubMsg.PeerLeft:
                var lid = r.U8();
                _names.Remove(lid);
                _toMod(Frames.PeerLeft(lid, r.Str()));
                break;
            case HubMsg.Data:
                if (_state != LinkState.Joined) return;
                byte from = r.U8();
                r.U8();
                _toMod(Frames.Recv(from, r.Bytes()));
                break;
            case HubMsg.Bye:
                var why = r.Remaining > 0 ? r.Str() : "host left";
                Leave("host said bye");
                SetState(LinkState.Ended, "The host ended the game (" + why + ")");
                break;
        }
    }

    /// <summary>Says goodbye to everyone and forgets the room. Keeps the Steam transport for later rooms.</summary>
    public void Leave(string reason)
    {
        if (_t != null)
        {
            var bye = new WireWriter().U8((byte)HubMsg.Bye).Str(reason).ToArray();
            if (_state == LinkState.Hosting) foreach (var g in _guests.Values) { _t.Send(g.Peer, bye, true); _t.Drop(g.Peer); }
            else if (_hostPeer != 0) { _t.Send(_hostPeer, bye, true); _t.Drop(_hostPeer); }
            _t.LeaveRoom();
            if (_t != _steam) _t.Dispose();
            _t = null;
        }
        _guests.Clear();
        _names.Clear();
        _hostPeer = 0;
        _lobby = "";
        _myId = PeerIds.None;
    }

    public void Dispose()
    {
        Leave("link closed");
        _steam?.Dispose();
        _steam = null;
    }
}

/// <summary>The one game payload kind the hub knows about: player states are forwarded unreliably.</summary>
static class GameMsgKinds
{
    public const byte PlayerState = 1;
}
