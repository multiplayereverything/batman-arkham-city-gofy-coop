using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BatCoop.Core;
using Steamworks;

namespace BatCoop.Link;

/// <summary>Steam lobbies + ISteamNetworkingMessages as AppID 480 (Spacewar). Follows coop-anything's
/// playbook/networking-steam-spacewar.md: friends-only lobby tagged with our protocol, accept sessions only
/// from lobby members (host) or the host (guest), keep-alives and timeouts live in the hub.</summary>
public sealed class SteamTransport : ITransport
{
    const string LobbyKey = "batcoop";
    const int Channel = 0;

    readonly Action<string> _log;
    readonly IntPtr[] _inbox = new IntPtr[64];
    readonly Dictionary<ulong, DateTime> _pendingSessions = [];
    CallResult<LobbyCreated_t>? _created;
    CallResult<LobbyEnter_t>? _entered;
    Callback<SteamNetworkingMessagesSessionRequest_t>? _sessionRequest;
    Callback<LobbyChatUpdate_t>? _chatUpdate;
    Callback<GameLobbyJoinRequested_t>? _joinRequested;
    CSteamID _lobby = CSteamID.Nil;
    ulong _host;
    bool _isHost;
    bool _ready;

    public string Name => "steam";
    public ConcurrentQueue<TransportEvent> Events { get; } = new();
    public string LocalName { get; private set; } = "";
    public bool Ready => _ready;

    public SteamTransport(Action<string> log) => _log = log;

    /// <summary>Starts Steam as Spacewar. Safe to call more than once.</summary>
    public bool Init(out string error)
    {
        error = "";
        if (_ready) return true;

        // A link started by the game inherits SteamAppId=200260 from Steam's launch of Arkham City. Without
        // this, SteamAPI would attach to the game's AppID instead of Spacewar.
        Environment.SetEnvironmentVariable("SteamAppId", "480");
        Environment.SetEnvironmentVariable("SteamGameId", "480");
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "steam_appid.txt"), "480"); } catch { }

        ESteamAPIInitResult r;
        try { r = SteamAPI.InitEx(out error); }
        catch (Exception e) { error = "steam_api64.dll failed to load: " + e.Message; return false; }
        if (r != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
        {
            error = r switch
            {
                ESteamAPIInitResult.k_ESteamAPIInitResult_NoSteamClient => "Steam is not running",
                ESteamAPIInitResult.k_ESteamAPIInitResult_VersionMismatch => "Steam client too old for steam_api64.dll",
                _ => string.IsNullOrEmpty(error) ? r.ToString() : error,
            };
            return false;
        }

        SteamNetworkingUtils.InitRelayNetworkAccess();
        LocalName = SteamFriends.GetPersonaName() ?? "";
        _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
        _chatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
        _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
        _ready = true;
        _log($"steam: ready as {LocalName} ({SteamUser.GetSteamID().m_SteamID}), AppID 480");
        return true;
    }

    public void StartHost(int maxPlayers, int directPort, bool friendsOnly)
    {
        if (!Init(out var err)) { Events.Enqueue(new Failed("Steam: " + err)); return; }
        LeaveLobby();
        _isHost = true;
        var type = friendsOnly ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePublic;
        _created = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        _created.Set(SteamMatchmaking.CreateLobby(type, maxPlayers));
    }

    void OnLobbyCreated(LobbyCreated_t r, bool ioFailure)
    {
        if (ioFailure || r.m_eResult != EResult.k_EResultOK)
        {
            Events.Enqueue(new Failed($"Steam could not create a lobby ({(ioFailure ? "I/O failure" : r.m_eResult.ToString())})"));
            return;
        }
        _lobby = new CSteamID(r.m_ulSteamIDLobby);
        _host = SteamUser.GetSteamID().m_SteamID;
        SteamMatchmaking.SetLobbyData(_lobby, LobbyKey, Versions.Protocol);
        SteamMatchmaking.SetLobbyData(_lobby, "name", LocalName);
        SteamFriends.SetRichPresence("connect", "+connect_lobby " + _lobby.m_SteamID);
        SteamFriends.SetRichPresence("status", "Batman: Arkham City co-op (BatCoop)");
        Events.Enqueue(new HostReady(_lobby.m_SteamID.ToString(), "Hosting a Steam lobby"));
    }

    public void StartJoin(string target)
    {
        if (!Init(out var err)) { Events.Enqueue(new Failed("Steam: " + err)); return; }
        LeaveLobby();
        _isHost = false;
        target = target.Trim();
        CSteamID lobby;
        if (target.Length == 0)
        {
            lobby = FindHostingFriend(out var who);
            if (!lobby.IsValid())
            {
                Events.Enqueue(new Failed("No Steam friend is hosting BatCoop right now (they need to press Host first)"));
                return;
            }
            Events.Enqueue(new TransportNotice($"Joining {who}'s game"));
        }
        else if (ulong.TryParse(target.Replace("+connect_lobby", "").Trim(), out var id))
        {
            lobby = new CSteamID(id);
        }
        else
        {
            Events.Enqueue(new Failed($"'{target}' is not a Steam lobby id"));
            return;
        }
        _entered = CallResult<LobbyEnter_t>.Create(OnLobbyEnter);
        _entered.Set(SteamMatchmaking.JoinLobby(lobby));
    }

    /// <summary>A friend playing Spacewar with a lobby is a candidate (HXCoop pattern); the lobby tag is
    /// checked after joining, because lobby data of a lobby we are not in may not be loaded yet.</summary>
    static CSteamID FindHostingFriend(out string who)
    {
        who = "";
        int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        for (int i = 0; i < n; i++)
        {
            var f = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            if (!SteamFriends.GetFriendGamePlayed(f, out var info)) continue;
            if (info.m_gameID.AppID().m_AppId != 480 || !info.m_steamIDLobby.IsValid()) continue;
            who = SteamFriends.GetFriendPersonaName(f);
            return info.m_steamIDLobby;
        }
        return CSteamID.Nil;
    }

    void OnLobbyEnter(LobbyEnter_t r, bool ioFailure)
    {
        if (ioFailure || r.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
        {
            Events.Enqueue(new Failed($"Could not enter the lobby ({(ioFailure ? "I/O failure" : ((EChatRoomEnterResponse)r.m_EChatRoomEnterResponse).ToString())})"));
            return;
        }
        _lobby = new CSteamID(r.m_ulSteamIDLobby);
        var tag = SteamMatchmaking.GetLobbyData(_lobby, LobbyKey);
        if (string.IsNullOrEmpty(tag))
        {
            LeaveLobby();
            Events.Enqueue(new Failed("That lobby is not a BatCoop game (someone playing the real Spacewar?)"));
            return;
        }
        _host = SteamMatchmaking.GetLobbyOwner(_lobby).m_SteamID;
        SteamFriends.SetRichPresence("connect", "+connect_lobby " + _lobby.m_SteamID);
        SteamFriends.SetRichPresence("status", "Batman: Arkham City co-op (BatCoop)");
        Events.Enqueue(new Connected(_host, $"In {SteamMatchmaking.GetLobbyData(_lobby, "name")}'s Steam lobby"));
    }

    bool IsLobbyMember(ulong id)
    {
        if (!_lobby.IsValid()) return false;
        int n = SteamMatchmaking.GetNumLobbyMembers(_lobby);
        for (int i = 0; i < n; i++)
            if (SteamMatchmaking.GetLobbyMemberByIndex(_lobby, i).m_SteamID == id) return true;
        return false;
    }

    void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t r)
    {
        var id = r.m_identityRemote.GetSteamID64();
        // Host: lobby members only (the member list can lag the request, so wait up to 10 s).
        // Guest: only the host.
        if (_isHost) _pendingSessions[id] = DateTime.UtcNow.AddSeconds(10);
        else if (id == _host) Accept(id);
        else _log($"steam: ignored a session request from {id}");
    }

    static void Accept(ulong id)
    {
        var ident = new SteamNetworkingIdentity();
        ident.SetSteamID64(id);
        SteamNetworkingMessages.AcceptSessionWithUser(ref ident);
    }

    void OnChatUpdate(LobbyChatUpdate_t u)
    {
        if (u.m_ulSteamIDLobby != _lobby.m_SteamID) return;
        const uint gone = (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected |
                                 EChatMemberStateChange.k_EChatMemberStateChangeKicked | EChatMemberStateChange.k_EChatMemberStateChangeBanned);
        if ((u.m_rgfChatMemberStateChange & gone) != 0)
            Events.Enqueue(new PeerGone(u.m_ulSteamIDUserChanged, "left the Steam lobby"));
    }

    void OnJoinRequested(GameLobbyJoinRequested_t r) =>
        Events.Enqueue(new InviteReceived(r.m_steamIDLobby.m_SteamID.ToString(), SteamFriends.GetFriendPersonaName(r.m_steamIDFriend)));

    public void Poll()
    {
        if (!_ready) return;
        SteamAPI.RunCallbacks();

        if (_pendingSessions.Count > 0)
        {
            foreach (var (id, deadline) in _pendingSessions.ToArray())
            {
                if (IsLobbyMember(id)) { Accept(id); _pendingSessions.Remove(id); }
                else if (DateTime.UtcNow > deadline) { _pendingSessions.Remove(id); _log($"steam: {id} is not in the lobby, session refused"); }
            }
        }

        int n;
        while ((n = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, _inbox, _inbox.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(_inbox[i]);
                var data = new byte[msg.m_cbSize];
                Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);
                var from = msg.m_identityPeer.GetSteamID64();
                SteamNetworkingMessage_t.Release(_inbox[i]);
                if (from != 0) Events.Enqueue(new Message(from, data));
            }
        }
    }

    public unsafe bool Send(ulong peer, byte[] data, bool reliable)
    {
        if (!_ready) return false;
        var ident = new SteamNetworkingIdentity();
        ident.SetSteamID64(peer);
        int flags = (reliable ? Constants.k_nSteamNetworkingSend_Reliable : Constants.k_nSteamNetworkingSend_Unreliable)
                    | Constants.k_nSteamNetworkingSend_NoNagle;
        fixed (byte* p = data)
        {
            for (int attempt = 0; ; attempt++)
            {
                var r = SteamNetworkingMessages.SendMessageToUser(ref ident, (IntPtr)p, (uint)data.Length, flags, Channel);
                if (r == EResult.k_EResultOK) return true;
                // A full send buffer on a reliable message: wait a moment and retry, never drop it.
                if (reliable && r == EResult.k_EResultLimitExceeded && attempt < 20) { Thread.Sleep(5); continue; }
                return false;
            }
        }
    }

    public void Drop(ulong peer)
    {
        if (!_ready) return;
        var ident = new SteamNetworkingIdentity();
        ident.SetSteamID64(peer);
        SteamNetworkingMessages.CloseSessionWithUser(ref ident);
    }

    public void Invite()
    {
        if (!_ready || !_lobby.IsValid()) return;
        // The link has no window, so the overlay usually cannot open; friends can still join with F6.
        SteamFriends.ActivateGameOverlayInviteDialog(_lobby);
        Events.Enqueue(new TransportNotice($"Friends join with F6 while you host, or with lobby id {_lobby.m_SteamID}"));
    }

    public void LeaveRoom() => LeaveLobby();

    void LeaveLobby()
    {
        if (_lobby.IsValid()) SteamMatchmaking.LeaveLobby(_lobby);
        _lobby = CSteamID.Nil;
        _host = 0;
        _pendingSessions.Clear();
        if (_ready) SteamFriends.ClearRichPresence();
    }

    public void Dispose()
    {
        if (!_ready) return;
        LeaveLobby();
        _created?.Dispose();
        _entered?.Dispose();
        _sessionRequest?.Dispose();
        _chatUpdate?.Dispose();
        _joinRequested?.Dispose();
        SteamAPI.Shutdown();
        _ready = false;
    }
}
