#nullable enable
// The mod's side of the local connection to BatCoopLink.exe. A reader thread turns frames into events;
// the game thread drains them with TryDequeue, so nothing here touches game objects.
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace BatCoop.Core;

public abstract record LinkEvent;
public sealed record StatusEvent(LinkState State, byte LocalId, string Text, string Lobby) : LinkEvent;
public sealed record PeerJoinedEvent(byte Id, string Name) : LinkEvent;
public sealed record PeerLeftEvent(byte Id, string Reason) : LinkEvent;
public sealed record RecvEvent(byte From, byte[] Payload) : LinkEvent;
public sealed record NoticeEvent(string Text) : LinkEvent;
public sealed record IdentityEvent(string Name) : LinkEvent;
public sealed record LinkLostEvent(string Reason) : LinkEvent;

public sealed class LinkClient : IDisposable
{
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly object _writeLock = new();
    readonly Thread _reader;
    volatile bool _closed;

    public ConcurrentQueue<LinkEvent> Events { get; } = new();
    public bool Connected => !_closed;

    LinkClient(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "BatCoop link reader" };
        _reader.Start();
    }

    /// <summary>Connects to a link on 127.0.0.1. Returns null when nothing listens there.</summary>
    /// <remarks>A plain blocking connect: on loopback it is answered or refused at once. Waiting on ConnectAsync
    /// from the game thread never finishes under BmSDK, whose synchronization context is that same thread.</remarks>
    public static LinkClient? TryConnect(int port)
    {
        var tcp = new TcpClient();
        try
        {
            tcp.Connect(IPAddress.Loopback, port);
            return new LinkClient(tcp);
        }
        catch
        {
            tcp.Dispose();
            return null;
        }
    }

    public bool Send(byte[] frame)
    {
        if (_closed) return false;
        try
        {
            lock (_writeLock) Framing.Write(_stream, frame);
            return true;
        }
        catch (Exception e)
        {
            Close("send failed: " + e.Message);
            return false;
        }
    }

    void ReadLoop()
    {
        try
        {
            while (!_closed)
            {
                var body = Framing.Read(_stream);
                if (body == null) { Close("link closed the connection"); return; }
                var ev = Parse(body);
                if (ev != null) Events.Enqueue(ev);
            }
        }
        catch (Exception e)
        {
            Close("link connection lost: " + e.Message);
        }
    }

    public static LinkEvent? Parse(byte[] body)
    {
        var r = new WireReader(body);
        switch ((LinkToMod)r.U8())
        {
            case LinkToMod.Status: return new StatusEvent((LinkState)r.U8(), r.U8(), r.Str(), r.Str());
            case LinkToMod.PeerJoined: return new PeerJoinedEvent(r.U8(), r.Str());
            case LinkToMod.PeerLeft: return new PeerLeftEvent(r.U8(), r.Str());
            case LinkToMod.Recv: return new RecvEvent(r.U8(), r.Bytes());
            case LinkToMod.Notice: return new NoticeEvent(r.Str());
            case LinkToMod.Identity: return new IdentityEvent(r.Str());
            default: return null;
        }
    }

    void Close(string reason)
    {
        if (_closed) return;
        _closed = true;
        Events.Enqueue(new LinkLostEvent(reason));
        try { _tcp.Close(); } catch { }
    }

    public void Dispose() => Close("closed by the mod");
}
