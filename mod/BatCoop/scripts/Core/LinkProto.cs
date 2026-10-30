#nullable enable
// Two protocols:
//  - local frames between the mod (inside the game) and BatCoopLink.exe over 127.0.0.1 TCP;
//  - hub envelopes between BatCoopLink processes (over Steam P2P or direct TCP).
// Both use the same length-prefixed framing on TCP: u32 body length, then the body.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;

namespace BatCoop.Core;

public static class Versions
{
    public const string Mod = "0.3.0";
    // Bump when anything on the wire changes; peers with a different value are refused at join.
    public const string Protocol = "batcoop-3";
    public const int DefaultLinkPort = 27960;
    public const int DefaultDirectPort = 27961;
    public const int MaxFrame = 4 * 1024 * 1024;
}

public enum TransportKind : byte { Steam = 0, Direct = 1 }

public enum LinkState : byte { Idle = 0, Connecting = 1, Hosting = 2, Joined = 3, Ended = 4 }

/// <summary>Frames from the mod to the link.</summary>
public enum ModToLink : byte
{
    Hello = 1,      // str modVersion, str protocol, str playerName
    Host = 2,       // u8 transport, u8 maxPlayers, u16 directPort, u8 friendsOnly
    Join = 3,       // u8 transport, str target ("" = find a hosting friend, a lobby id, or host:port)
    Leave = 4,
    Send = 5,       // u8 to (255 = everyone), u8 reliable, bytes payload
    Invite = 6,     // ask Steam to show the invite dialog (best effort)
}

/// <summary>Frames from the link to the mod.</summary>
public enum LinkToMod : byte
{
    Status = 101,     // u8 state, u8 localId, str text, str lobby
    PeerJoined = 102, // u8 id, str name
    PeerLeft = 103,   // u8 id, str reason
    Recv = 104,       // u8 from, bytes payload
    Notice = 105,     // str text (one-off message for the HUD)
    Identity = 106,   // str name: the name other players see (the ini Name, else the Steam name)
}

/// <summary>Envelopes between link processes.</summary>
public enum HubMsg : byte
{
    JoinReq = 1,    // str protocol, str name
    JoinAck = 2,    // u8 yourId, u8 maxPlayers, u8 n, n x (u8 id, str name)
    JoinDeny = 3,   // str reason
    Data = 4,       // u8 from, u8 to, bytes payload
    PeerJoined = 5, // u8 id, str name
    PeerLeft = 6,   // u8 id, str reason
    KeepAlive = 7,
    Bye = 8,        // str reason
}

public static class PeerIds
{
    public const byte Host = 0;
    public const byte Everyone = 255;
    public const byte None = 254;
}

/// <summary>Blocking length-prefixed frame IO on a socket stream.</summary>
public static class Framing
{
    public static void Write(Stream s, byte[] body)
    {
        var len = BitConverter.GetBytes((uint)body.Length);
        // One write call keeps frames whole when several threads share a stream under a lock.
        var buf = new byte[4 + body.Length];
        Buffer.BlockCopy(len, 0, buf, 0, 4);
        Buffer.BlockCopy(body, 0, buf, 4, body.Length);
        s.Write(buf, 0, buf.Length);
    }

    /// <summary>Returns null on a clean end of stream.</summary>
    public static byte[]? Read(Stream s)
    {
        var head = new byte[4];
        if (!ReadExactly(s, head)) return null;
        var len = BitConverter.ToUInt32(head, 0);
        if (len > Versions.MaxFrame) throw new WireException($"frame too large: {len}");
        var body = new byte[len];
        if (!ReadExactly(s, body)) throw new WireException("stream ended inside a frame");
        return body;
    }

    static bool ReadExactly(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0)
            {
                if (got == 0) return false;
                throw new WireException("stream ended inside a frame");
            }
            got += n;
        }
        return true;
    }
}

/// <summary>Builders for the frames above, so every side encodes them the same way.</summary>
public static class Frames
{
    public static byte[] Hello(string name) =>
        new WireWriter().U8((byte)ModToLink.Hello).Str(Versions.Mod).Str(Versions.Protocol).Str(name).ToArray();

    public static byte[] Host(TransportKind t, int maxPlayers, int directPort, bool friendsOnly) =>
        new WireWriter().U8((byte)ModToLink.Host).U8((byte)t).U8((byte)maxPlayers).U16((ushort)directPort).Bool(friendsOnly).ToArray();

    public static byte[] Join(TransportKind t, string target) =>
        new WireWriter().U8((byte)ModToLink.Join).U8((byte)t).Str(target).ToArray();

    public static byte[] Leave() => [(byte)ModToLink.Leave];
    public static byte[] Invite() => [(byte)ModToLink.Invite];

    public static byte[] Send(byte to, bool reliable, byte[] payload) =>
        new WireWriter().U8((byte)ModToLink.Send).U8(to).Bool(reliable).Bytes(payload).ToArray();

    public static byte[] Status(LinkState st, byte localId, string text, string lobby) =>
        new WireWriter().U8((byte)LinkToMod.Status).U8((byte)st).U8(localId).Str(text).Str(lobby).ToArray();

    public static byte[] PeerJoined(byte id, string name) =>
        new WireWriter().U8((byte)LinkToMod.PeerJoined).U8(id).Str(name).ToArray();

    public static byte[] PeerLeft(byte id, string reason) =>
        new WireWriter().U8((byte)LinkToMod.PeerLeft).U8(id).Str(reason).ToArray();

    public static byte[] Recv(byte from, byte[] payload) =>
        new WireWriter().U8((byte)LinkToMod.Recv).U8(from).Bytes(payload).ToArray();

    public static byte[] Notice(string text) =>
        new WireWriter().U8((byte)LinkToMod.Notice).Str(text).ToArray();

    public static byte[] Identity(string name) =>
        new WireWriter().U8((byte)LinkToMod.Identity).Str(name).ToArray();
}
