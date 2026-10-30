#nullable enable
// Game-agnostic co-op session: talks to the link, sends the local player's state, keeps one
// interpolation buffer per remote player. The game layer and the lab's fake world both drive it.
using System;
using System.Collections.Generic;

namespace BatCoop.Core;

public interface ICoopWorld
{
    /// <summary>Seconds on a steady clock.</summary>
    double Now { get; }
    /// <summary>The local player's current state (Seq and Time are filled in by the session). False when there is no pawn.</summary>
    bool TryReadLocal(out PlayerState state);
    PlayerInfo LocalInfo { get; }
}

public sealed class RemotePlayer(byte id, string name)
{
    public byte Id { get; } = id;
    public string Name { get; set; } = name;
    public PlayerInfo? Info { get; set; }
    public SnapshotBuffer Buffer { get; } = new();
    public double LastRecv { get; set; }
    public long StatesReceived { get; set; }
    public double RttMs { get; set; } = -1;
    /// <summary>This player's pose transitions, waiting to be replayed on its puppet.</summary>
    public PoseQueue Poses { get; } = new();
}

public sealed class CoopSession : IDisposable
{
    readonly Action<string> _log;
    readonly Dictionary<byte, RemotePlayer> _remotes = [];
    LinkClient? _link;
    uint _seq;
    double _nextState, _nextPing, _nextWorld;
    PlayerInfo? _sentInfo;

    public CoopSession(Action<string> log)
    {
        _log = log;
        World = new WorldSync(log, SendGame);
        NpcHost = new NpcHost(SendGame);
        NpcGuest = new NpcGuest(SendGame);
        Vote = new CutsceneVote(SendGame);
        Pvp = new PvpState((payload, to) => SendGame(to, true, payload));
        World.StepCommitted += OnCommitted;
    }

    public WorldSync World { get; }
    public NpcHost NpcHost { get; }
    public NpcGuest NpcGuest { get; }
    public CutsceneVote Vote { get; }
    public PvpState Pvp { get; }
    /// <summary>Chat and event lines (what the chat box shows).</summary>
    public ChatLog Feed { get; } = new();
    /// <summary>The host's game settings; the host changes them with SetHostSettings, guests receive them.</summary>
    public HostSettings Settings { get; private set; } = new();
    double _now;
    /// <summary>Set by the game layer (or the lab); null disables that kind of sync.</summary>
    public IWorldPort? WorldPort { get; set; }
    public INpcHostPort? NpcPort { get; set; }

    public double SendHz { get; set; } = 20;
    public LinkState State { get; private set; } = LinkState.Idle;
    public byte LocalId { get; private set; } = PeerIds.None;
    public string StatusText { get; private set; } = "Not connected";
    public string Lobby { get; private set; } = "";
    public bool LinkUp => _link is { Connected: true };
    public bool InRoom => State is LinkState.Hosting or LinkState.Joined;
    public bool IsHost => State == LinkState.Hosting;
    public WorldInfo? HostWorld { get; private set; }
    public string LocalWorld { get; set; } = "";
    public IReadOnlyDictionary<byte, RemotePlayer> Remotes => _remotes;
    public List<string> Notices { get; } = [];
    public long StatesSent { get; private set; }
    /// <summary>Our name as other players see it, once the link has answered (the ini Name, else the Steam name).</summary>
    public string LocalName { get; private set; } = "";

    /// <summary>Raised on the thread that calls Update, for every chat line from a peer.</summary>
    public event Action<RemotePlayer, string>? Chat;

    public void Attach(LinkClient link, string playerName)
    {
        _link?.Dispose();
        _link = link;
        link.Send(Frames.Hello(playerName));
    }

    public bool Host(TransportKind t, int maxPlayers, int directPort, bool friendsOnly = true) =>
        _link?.Send(Frames.Host(t, maxPlayers, directPort, friendsOnly)) ?? false;

    public bool Join(TransportKind t, string target) => _link?.Send(Frames.Join(t, target)) ?? false;

    public void Leave()
    {
        _link?.Send(Frames.Leave());
        _remotes.Clear();
    }

    public void Invite() => _link?.Send(Frames.Invite());

    public void SendChat(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 200) text = text[..200];
        SendGame(PeerIds.Everyone, true, GameMessages.Chat(text));
        Feed.Add(new FeedLine(FeedKind.Chat, LocalName.Length > 0 ? LocalName : "You", text, _now));
    }

    /// <summary>An event line for everyone (and for us).</summary>
    public void SendEvent(FeedKind kind, string who, string text)
    {
        if (!Settings.EventFeed) return;
        SendGame(PeerIds.Everyone, true, FeedMessages.Event(kind, who, text));
        Feed.Add(new FeedLine(kind, who, text, _now));
    }

    /// <summary>The name of a player in this room (us included).</summary>
    public string NameOf(byte id) => id == LocalId ? (LocalName.Length > 0 ? LocalName : "You")
        : _remotes.TryGetValue(id, out var rp) ? rp.Name : $"Player {id + 1}";

    /// <summary>Host: new settings for everyone.</summary>
    public void SetHostSettings(HostSettings s)
    {
        Settings = s;
        Pvp.Enabled = s.Pvp;
        if (IsHost) SendGame(PeerIds.Everyone, true, s.Encode());
    }

    void OnCommitted(IReadOnlyList<WorldEffect> diff, WorldState before, byte credit)
    {
        var what = MissionFeed.Describe(diff, before);
        if (what != null) SendEvent(FeedKind.Mission, NameOf(credit), what);
    }

    /// <summary>Game payloads from the game layer (animation, PVP, events) to everyone, or to one player.</summary>
    public void Send(byte[] payload, bool reliable = true, byte to = PeerIds.Everyone) => SendGame(to, reliable, payload);

    void SendGame(byte to, bool reliable, byte[] payload)
    {
        if (InRoom) _link?.Send(Frames.Send(to, reliable, payload));
    }

    void Notice(string text)
    {
        _log(text);
        Notices.Add(text);
        if (Notices.Count > 8) Notices.RemoveAt(0);
    }

    public void Update(ICoopWorld world)
    {
        double now = world.Now;
        _now = now;
        Drain(now);
        if (!InRoom) return;

        if (now >= _nextState)
        {
            // Keep the cadence when frames are uneven; after a long stall, start over instead of bursting.
            double period = 1.0 / SendHz;
            _nextState = Math.Max(_nextState + period, now - period);
            if (world.TryReadLocal(out var st))
            {
                st.Seq = ++_seq;
                st.Time = now;
                SendGame(PeerIds.Everyone, false, st.Encode());
                StatesSent++;
            }
        }

        var info = world.LocalInfo;
        if (info != _sentInfo)
        {
            _sentInfo = info;
            SendGame(PeerIds.Everyone, true, info.Encode());
        }

        if (IsHost && now >= _nextWorld)
        {
            _nextWorld = now + 5;
            SendGame(PeerIds.Everyone, true, new WorldInfo(LocalWorld, info.Name).Encode());
        }

        if (WorldPort != null)
        {
            if (IsHost) World.HostTick(WorldPort, now);
            else World.GuestTick(WorldPort, now);
        }
        if (IsHost && NpcPort != null)
        {
            var players = new List<System.Numerics.Vector3>();
            if (world.TryReadLocal(out var me)) players.Add(me.Pos);
            foreach (var rp in _remotes.Values)
                if (rp.Buffer.HasData && rp.Buffer.Newest.InWorld) players.Add(rp.Buffer.Newest.Pos);
            NpcHost.Tick(NpcPort, players, now);
        }

        if (now >= _nextPing)
        {
            _nextPing = now + 2;
            SendGame(PeerIds.Everyone, true, GameMessages.Ping(now));
        }
    }

    void Drain(double now)
    {
        if (_link == null) return;
        while (_link.Events.TryDequeue(out var ev))
        {
            switch (ev)
            {
                case StatusEvent s:
                    var wasInRoom = InRoom;
                    State = s.State;
                    LocalId = s.LocalId;
                    StatusText = s.Text;
                    Lobby = s.Lobby;
                    if (!InRoom) { _remotes.Clear(); HostWorld = null; }
                    if (InRoom != wasInRoom) ResetRoomState();
                    if (InRoom && !wasInRoom) { _sentInfo = null; _nextWorld = 0; }
                    _log($"status {s.State} id={s.LocalId} '{s.Text}' lobby={s.Lobby}");
                    break;
                case PeerJoinedEvent j:
                    if (j.Id == LocalId) break;
                    _remotes[j.Id] = new RemotePlayer(j.Id, j.Name) { LastRecv = now };
                    _sentInfo = null; // the newcomer needs our info
                    if (IsHost)
                    {
                        _nextWorld = 0;
                        World.HostSendSnapshot(j.Id);
                        NpcHost.SendAllInfo(j.Id);
                        SendGame(j.Id, true, Settings.Encode());
                    }
                    Notice($"{j.Name} joined");
                    Feed.Add(new FeedLine(FeedKind.Joined, j.Name, "", now));
                    break;
                case PeerLeftEvent l:
                    if (_remotes.Remove(l.Id, out var gone))
                    {
                        Notice($"{gone.Name} left ({l.Reason})");
                        Feed.Add(new FeedLine(FeedKind.Left, gone.Name, "", now));
                    }
                    if (IsHost) Vote.HostPlayerLeft(l.Id);
                    break;
                case RecvEvent r:
                    OnGame(r.From, r.Payload, now);
                    break;
                case NoticeEvent n:
                    Notice(n.Text);
                    break;
                case IdentityEvent idn:
                    LocalName = idn.Name;
                    _log($"playing as '{idn.Name}'");
                    break;
                case LinkLostEvent lost:
                    State = LinkState.Idle;
                    ResetRoomState();
                    StatusText = "Link stopped: " + lost.Reason;
                    _remotes.Clear();
                    HostWorld = null;
                    _link = null;
                    _log(StatusText);
                    return;
            }
        }
    }

    void OnGame(byte from, byte[] payload, double now)
    {
        if (from == LocalId || payload.Length == 0) return;
        if (!_remotes.TryGetValue(from, out var rp))
        {
            // Data can overtake the join notice; create the player and fill the name in later.
            rp = new RemotePlayer(from, $"Player {from}");
            _remotes[from] = rp;
        }
        rp.LastRecv = now;
        try
        {
            var r = new WireReader(payload);
            switch ((GameMsg)r.U8())
            {
                case GameMsg.PlayerState:
                    var st = PlayerState.Decode(ref r);
                    if (rp.Buffer.Add(st, now)) rp.StatesReceived++;
                    break;
                case GameMsg.PlayerInfo:
                    rp.Info = PlayerInfo.Decode(ref r);
                    if (rp.Info.Name.Length > 0) rp.Name = rp.Info.Name;
                    break;
                case GameMsg.Chat:
                    var line = r.Str();
                    Feed.Add(new FeedLine(FeedKind.Chat, rp.Name, line, now));
                    Chat?.Invoke(rp, line);
                    break;
                case GameMsg.Event:
                    var ev = FeedMessages.DecodeEvent(ref r, now);
                    if (Settings.EventFeed) Feed.Add(ev);
                    break;
                case GameMsg.Settings:
                    if (from == PeerIds.Host && !IsHost)
                    {
                        Settings = HostSettings.Decode(ref r);
                        Pvp.Enabled = Settings.Pvp;
                        _log($"host settings: PVP {(Settings.Pvp ? "on" : "off")}, damage {Settings.PvpDamagePercent}%, teleport {Settings.AllowTeleport}, skip {Settings.CutsceneSkip}");
                    }
                    break;
                case GameMsg.Pvp:
                    Pvp.Receive(from, ref r, now);
                    break;
                case GameMsg.Ping:
                    SendGame(from, true, GameMessages.Pong(r.F32()));
                    break;
                case GameMsg.Pong:
                    rp.RttMs = (now - r.F32()) * 1000.0;
                    break;
                case GameMsg.WorldInfo:
                    if (from == PeerIds.Host) HostWorld = WorldInfo.Decode(ref r);
                    break;
                case GameMsg.World:
                    if (WorldPort == null) break;
                    if (IsHost) World.HostReceive(from, ref r, WorldPort);
                    else if (from == PeerIds.Host) World.GuestReceive(ref r, WorldPort, now);
                    break;
                case GameMsg.Npc:
                    if (IsHost) { if (NpcPort != null) NpcHost.Receive(from, ref r, NpcPort); }
                    else if (from == PeerIds.Host) NpcGuest.Receive(ref r, now);
                    break;
                case GameMsg.Anim:
                    var call = PoseCall.Decode(ref r, out var aseq);
                    rp.Poses.Add(aseq, call, now);
                    break;
                case GameMsg.Vote:
                    if (IsHost) Vote.HostReceive(from, ref r);
                    else if (from == PeerIds.Host) Vote.GuestReceive(ref r);
                    break;
            }
        }
        catch (WireException e)
        {
            _log($"bad message from {from}: {e.Message}");
        }
    }

    void ResetRoomState()
    {
        Pvp.Reset();
        World.Reset();
        NpcGuest.Clear();
        Vote.Reset();
        if (IsHost) NpcHost.Reset();
    }

    public void Dispose()
    {
        _link?.Dispose();
        _link = null;
    }
}
