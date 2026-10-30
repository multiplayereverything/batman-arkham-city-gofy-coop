// BatCoopLink: the network side of BatCoop. The game mod starts it and talks to it over 127.0.0.1;
// it runs Steam as Spacewar (AppID 480) or plain TCP, and carries the mod's messages between players.
//
//   BatCoopLink.exe [--port 27960] [--steam] [--parent <pid>] [--stay] [--log <file>] [--timeout <s>]
//
// --steam   start Steam right away (so invites work before hosting); otherwise on first use
// --parent  exit when that process (the game) exits
// --stay    keep running when the mod disconnects (lab use)
//
//   BatCoopLink.exe --fix-display <game pid> <BmEngine.ini> <W>x<H> fullscreen|windowed
// waits for the game to quit (it saves its resolution then), then writes this resolution (see DisplaySettings).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BatCoop.Core;
using BatCoop.Link;

if (args.Length == 5 && args[0] == "--fix-display")
    return FixDisplay.Run(int.Parse(args[1]), args[2], args[3], args[4]);

int port = Versions.DefaultLinkPort;
bool steamEarly = false, stay = false;
int parentPid = 0;
string? logPath = null;
double timeout = 20;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--steam": steamEarly = true; break;
        case "--stay": stay = true; break;
        case "--parent": parentPid = int.Parse(args[++i]); break;
        case "--log": logPath = args[++i]; break;
        case "--timeout": timeout = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
    }
}

var logLock = new object();
StreamWriter? logFile = null;
if (logPath != null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
    logFile = new StreamWriter(logPath, append: false) { AutoFlush = true };
}
void Log(string s)
{
    var line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
    lock (logLock)
    {
        Console.WriteLine(line);
        logFile?.WriteLine(line);
    }
}

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
try { listener.Start(); }
catch (SocketException e)
{
    Log($"port {port} is busy ({e.SocketErrorCode}); another BatCoopLink is probably running");
    return 3;
}
Log($"BatCoopLink {Versions.Mod} ({Versions.Protocol}) on 127.0.0.1:{port}, pid {Environment.ProcessId}");

var inbox = new ConcurrentQueue<(int conn, byte[]? frame)>();
var wake = new AutoResetEvent(false);
object modLock = new();
NetworkStream? modStream = null;
int modConn = 0;
bool hadMod = false;

void ToMod(byte[] frame)
{
    lock (modLock)
    {
        if (modStream == null) return;
        try { Framing.Write(modStream, frame); }
        catch (Exception e) { Log("write to mod failed: " + e.Message); }
    }
}

new Thread(() =>
{
    while (true)
    {
        TcpClient tcp;
        try { tcp = listener.AcceptTcpClient(); } catch { return; }
        tcp.NoDelay = true;
        int id;
        lock (modLock)
        {
            // A new mod connection (for example after a script hot reload) replaces the old one.
            modStream?.Close();
            modStream = tcp.GetStream();
            id = ++modConn;
        }
        Log($"mod connected (#{id})");
        var stream = tcp.GetStream();
        new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var f = Framing.Read(stream);
                    if (f == null) break;
                    inbox.Enqueue((id, f));
                    wake.Set();
                }
            }
            catch (Exception e) { Log($"mod #{id} read error: {e.Message}"); }
            inbox.Enqueue((id, null));
            wake.Set();
        }) { IsBackground = true, Name = "mod reader" }.Start();
    }
}) { IsBackground = true, Name = "mod accept" }.Start();

ITransport MakeTransport(TransportKind k) => k == TransportKind.Steam ? new SteamTransport(Log) : new DirectTransport(Log);

// The player's Steam name is their default co-op name, on the direct transport too. Steam starts once (as
// Spacewar) and the same transport serves Steam rooms later.
SteamTransport? steamUp = null;
bool steamTried = false;
Hub? hubRef = null;
string SteamName()
{
    if (!steamTried)
    {
        steamTried = true;
        var st = new SteamTransport(Log);
        if (st.Init(out var err)) { steamUp = st; hubRef?.UseSteamEarly(st); }
        else Log("steam: not started: " + err);
    }
    return steamUp?.LocalName ?? "";
}

using var hub = new Hub(ToMod, Log, MakeTransport, SteamName) { PeerTimeout = timeout };
hubRef = hub;
if (steamEarly) SteamName();

Process? parent = null;
if (parentPid != 0)
{
    try { parent = Process.GetProcessById(parentPid); }
    catch { Log($"parent {parentPid} is not running"); return 4; }
}

var clock = Stopwatch.StartNew();
var exitCode = 0;
while (true)
{
    wake.WaitOne(5);
    while (inbox.TryDequeue(out var item))
    {
        if (item.frame == null)
        {
            bool current;
            lock (modLock) current = item.conn == modConn;
            if (!current) continue;
            lock (modLock) modStream = null;
            Log("mod disconnected");
            if (!stay) goto done;
            hub.Leave("the player's game closed");
            continue;
        }
        hadMod = true;
        try { hub.OnModFrame(item.frame); }
        catch (WireException e) { Log("bad frame from mod: " + e.Message); }
    }
    hub.Tick(clock.Elapsed.TotalSeconds);
    if (parent != null && parent.HasExited) { Log("game exited"); goto done; }
    if (!hadMod && !stay && clock.Elapsed.TotalSeconds > 60) { Log("no mod connected within 60 s"); exitCode = 5; goto done; }
}

done:
listener.Stop();
Log("bye");
return exitCode;
