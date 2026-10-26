using System.Diagnostics;
using System.Numerics;
using BatCoop.Core;

namespace BatCoop.Lab;

/// <summary>A scripted player: position, facing and velocity come from a path function of time.</summary>
public sealed class FakeWorld(string name, Func<double, (Vector3 pos, float yaw, Vector3 vel)> path) : ICoopWorld
{
    public static readonly Stopwatch Clock = Stopwatch.StartNew();
    public double Now => Clock.Elapsed.TotalSeconds;
    public bool InWorld = true;
    public PlayerInfo LocalInfo { get; set; } = new(name, CharacterId.Batman, "RPawnPlayerBm", "batentry");

    public (Vector3 pos, float yaw, Vector3 vel) Truth(double t) => path(t);

    public bool TryReadLocal(out PlayerState s)
    {
        s = default;
        if (!InWorld) return false;
        var (pos, yaw, vel) = path(Now);
        s.Pos = pos;
        s.Yaw = yaw;
        s.Vel = vel;
        s.Physics = 1;
        s.Flags = PlayerFlags.InWorld;
        s.Health = 100;
        s.HealthMax = 100;
        return true;
    }

    public static Func<double, (Vector3, float, Vector3)> Circle(Vector3 center, float radius, float speed, float phase = 0) => t =>
    {
        double ang = phase + t * speed / radius;
        var pos = center + new Vector3((float)Math.Cos(ang) * radius, (float)Math.Sin(ang) * radius, 0);
        var dir = new Vector3(-(float)Math.Sin(ang), (float)Math.Cos(ang), 0);
        float yaw = (float)(Math.Atan2(dir.Y, dir.X) * 180 / Math.PI);
        return (pos, yaw, dir * speed);
    };

    public static Func<double, (Vector3, float, Vector3)> Still(Vector3 pos) => _ => (pos, 0f, Vector3.Zero);
}

/// <summary>A bot: its own BatCoopLink.exe process plus the mod's CoopSession, like one game copy.</summary>
public sealed class Bot : IDisposable
{
    public readonly string Name;
    public readonly FakeWorld World;
    public readonly CoopSession Session;
    public readonly int LinkPort;
    public readonly List<string> Log = [];
    readonly Process _link;

    public Bot(string name, int linkPort, FakeWorld world, string linkExe, string logDir)
    {
        Name = name;
        World = world;
        LinkPort = linkPort;
        var psi = new ProcessStartInfo(linkExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "--port", linkPort.ToString(), "--parent", Environment.ProcessId.ToString(),
                                   "--log", Path.Combine(logDir, $"link-{name}.log"), "--timeout", "5" })
            psi.ArgumentList.Add(a);
        _link = Process.Start(psi)!;
        _link.OutputDataReceived += (_, _) => { };
        _link.BeginOutputReadLine();
        _link.BeginErrorReadLine();

        LinkClient? c = null;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (c == null && DateTime.UtcNow < until)
        {
            c = LinkClient.TryConnect(linkPort, 200);
            if (c == null) Thread.Sleep(100);
        }
        if (c == null) throw new Exception($"{name}: link on port {linkPort} did not come up");
        Session = new CoopSession(s => { lock (Log) Log.Add($"{World.Now:0.000} {s}"); });
        Session.Attach(c, name);
    }

    public void Tick() => Session.Update(World);

    public bool LinkExited => _link.HasExited;

    public void Dispose()
    {
        Session.Dispose();
        if (!_link.WaitForExit(3000)) { try { _link.Kill(); } catch { } }
    }

    public void Kill()
    {
        try { _link.Kill(); } catch { }
        Session.Dispose();
    }
}
