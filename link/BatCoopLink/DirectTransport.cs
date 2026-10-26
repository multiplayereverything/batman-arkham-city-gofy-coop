using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using BatCoop.Core;

namespace BatCoop.Link;

/// <summary>Plain TCP between links: the host listens, guests connect to host:port. For LAN, VPNs,
/// forwarded ports and the one-PC lab. Every envelope is a length-prefixed frame; TCP is always reliable.</summary>
public sealed class DirectTransport(Action<string> log) : ITransport
{
    sealed class Conn(ulong id, TcpClient tcp)
    {
        public ulong Id = id;
        public TcpClient Tcp = tcp;
        public NetworkStream Stream = tcp.GetStream();
        public readonly object WriteLock = new();
        public volatile bool Closed;
    }

    readonly ConcurrentDictionary<ulong, Conn> _conns = new();
    TcpListener? _listener;
    long _nextId;
    volatile bool _disposed;

    public string Name => "direct";
    public string LocalName => "";
    public ConcurrentQueue<TransportEvent> Events { get; } = new();

    public void StartHost(int maxPlayers, int directPort, bool friendsOnly)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, directPort);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
            _listener.Start();
        }
        catch (Exception e)
        {
            Events.Enqueue(new Failed($"cannot listen on port {directPort}: {e.Message}"));
            return;
        }
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Events.Enqueue(new HostReady($"{LocalAddress()}:{port}", $"Hosting on port {port} (direct)"));
        new Thread(AcceptLoop) { IsBackground = true, Name = "direct accept" }.Start();
    }

    static string LocalAddress()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("10.255.255.255", 9); // no packet is sent; this only picks the outgoing interface
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }

    void AcceptLoop()
    {
        while (!_disposed && _listener != null)
        {
            TcpClient tcp;
            try { tcp = _listener.AcceptTcpClient(); }
            catch { return; }
            tcp.NoDelay = true;
            var c = new Conn((ulong)Interlocked.Increment(ref _nextId), tcp);
            _conns[c.Id] = c;
            log($"direct: connection {c.Id} from {tcp.Client.RemoteEndPoint}");
            StartReader(c);
        }
    }

    public void StartJoin(string target)
    {
        new Thread(() =>
        {
            var (host, port) = ParseTarget(target);
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                if (!tcp.ConnectAsync(host, port).Wait(5000)) throw new TimeoutException("no answer in 5 s");
            }
            catch (Exception e)
            {
                tcp.Dispose();
                Events.Enqueue(new Failed($"cannot reach {host}:{port}: {(e is AggregateException a ? a.InnerException?.Message : e.Message)}"));
                return;
            }
            var c = new Conn((ulong)Interlocked.Increment(ref _nextId), tcp);
            _conns[c.Id] = c;
            Events.Enqueue(new Connected(c.Id, $"Connected to {host}:{port}"));
            StartReader(c);
        }) { IsBackground = true, Name = "direct join" }.Start();
    }

    public static (string host, int port) ParseTarget(string target)
    {
        target = target.Trim();
        int colon = target.LastIndexOf(':');
        if (colon > 0 && int.TryParse(target[(colon + 1)..], out var p)) return (target[..colon], p);
        return (target.Length == 0 ? "127.0.0.1" : target, Versions.DefaultDirectPort);
    }

    void StartReader(Conn c)
    {
        new Thread(() =>
        {
            string reason = "connection closed";
            try
            {
                while (!c.Closed)
                {
                    var body = Framing.Read(c.Stream);
                    if (body == null) break;
                    Events.Enqueue(new Message(c.Id, body));
                }
            }
            catch (Exception e) { reason = e.Message; }
            if (!c.Closed) Events.Enqueue(new PeerGone(c.Id, reason));
            Close(c);
        }) { IsBackground = true, Name = $"direct reader {c.Id}" }.Start();
    }

    public void Poll() { }

    public bool Send(ulong peer, byte[] data, bool reliable)
    {
        if (!_conns.TryGetValue(peer, out var c) || c.Closed) return false;
        try
        {
            lock (c.WriteLock) Framing.Write(c.Stream, data);
            return true;
        }
        catch (Exception e)
        {
            if (!c.Closed) Events.Enqueue(new PeerGone(peer, e.Message));
            Close(c);
            return false;
        }
    }

    public void Drop(ulong peer)
    {
        if (_conns.TryGetValue(peer, out var c)) Close(c);
    }

    void Close(Conn c)
    {
        c.Closed = true;
        _conns.TryRemove(c.Id, out _);
        try { c.Tcp.Close(); } catch { }
    }

    public void Invite() => Events.Enqueue(new TransportNotice("Direct mode: give your friends your IP address and port"));

    public void LeaveRoom() => Dispose();

    public void Dispose()
    {
        _disposed = true;
        try { _listener?.Stop(); } catch { }
        foreach (var c in _conns.Values) Close(c);
    }
}
