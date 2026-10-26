using System.Collections.Concurrent;

namespace BatCoop.Link;

/// <summary>Something that moves hub envelopes between link processes. Peers are opaque 64-bit handles
/// (a SteamID for Steam, a connection number for direct TCP).</summary>
public interface ITransport : IDisposable
{
    string Name { get; }
    ConcurrentQueue<TransportEvent> Events { get; }

    /// <summary>Open a room. Raises HostReady (with a lobby id or address) or Failed.</summary>
    void StartHost(int maxPlayers, int directPort, bool friendsOnly);

    /// <summary>Connect to a room. Raises Connected (with the host's peer handle) or Failed.</summary>
    void StartJoin(string target);

    /// <summary>Called often on the hub thread.</summary>
    void Poll();

    bool Send(ulong peer, byte[] data, bool reliable);
    void Drop(ulong peer);

    /// <summary>Forget the current room (leave the lobby, close the listener) but stay usable.</summary>
    void LeaveRoom();

    /// <summary>The local player's name on this transport, or empty.</summary>
    string LocalName { get; }

    /// <summary>Best effort: show a way to invite friends.</summary>
    void Invite();
}

public abstract record TransportEvent;
public sealed record HostReady(string Lobby, string Text) : TransportEvent;
public sealed record Connected(ulong HostPeer, string Text) : TransportEvent;
public sealed record Failed(string Reason) : TransportEvent;
public sealed record Message(ulong Peer, byte[] Data) : TransportEvent;
public sealed record PeerGone(ulong Peer, string Reason) : TransportEvent;
public sealed record TransportNotice(string Text) : TransportEvent;
/// <summary>A friend invited us (Steam) while idle; the hub may join on its own.</summary>
public sealed record InviteReceived(string Target, string From) : TransportEvent;
