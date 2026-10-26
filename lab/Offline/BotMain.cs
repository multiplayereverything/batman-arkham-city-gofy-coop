using System.Globalization;
using System.Numerics;
using BatCoop.Core;

namespace BatCoop.Lab;

/// <summary>One bot player that joins a real game copy: <c>Offline bot --link-exe X --join host:port ...</c>.
/// Walks a circle, writes an animcheck probe stream, prints a JSON summary line at the end.</summary>
public static class BotMain
{
    public static int Run(string[] args)
    {
        string linkExe = "", join = "127.0.0.1:27961", probe = "", outDir = ".", name = "LabBot";
        var center = Vector3.Zero;
        float radius = 400, speed = 300;
        double seconds = 30, waitJoin = 30;
        int linkPort = 38960;
        var ci = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--link-exe": linkExe = args[++i]; break;
                case "--join": join = args[++i]; break;
                case "--probe": probe = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--name": name = args[++i]; break;
                case "--link-port": linkPort = int.Parse(args[++i]); break;
                case "--radius": radius = float.Parse(args[++i], ci); break;
                case "--speed": speed = float.Parse(args[++i], ci); break;
                case "--seconds": seconds = double.Parse(args[++i], ci); break;
                case "--wait-join": waitJoin = double.Parse(args[++i], ci); break;
                case "--center":
                    var p = args[++i].Split(',').Select(x => float.Parse(x, ci)).ToArray();
                    center = new Vector3(p[0], p[1], p[2]);
                    break;
                default: Console.Error.WriteLine("unknown bot argument " + args[i]); return 2;
            }
        }
        Directory.CreateDirectory(outDir);
        // Start the circle so that its first point is the given center plus radius on X (beside the host).
        var path = FakeWorld.Circle(center - new Vector3(radius, 0, 0), radius, speed);
        using var bot = new Bot(name, linkPort, new FakeWorld(name, path), linkExe, outDir);
        bot.Session.Join(TransportKind.Direct, join);

        var end = DateTime.UtcNow.AddSeconds(waitJoin);
        while (DateTime.UtcNow < end && bot.Session.State != LinkState.Joined && bot.Session.State != LinkState.Ended)
        {
            bot.Tick();
            Thread.Sleep(10);
        }
        Console.WriteLine($"bot: {bot.Session.State} '{bot.Session.StatusText}' id={bot.Session.LocalId}");
        if (bot.Session.State != LinkState.Joined) return 1;

        ProbeWriter? pw = probe.Length > 0 ? new ProbeWriter(probe, "bot") : null;
        double next = 0, stop = bot.World.Now + seconds;
        while (bot.World.Now < stop && bot.Session.State == LinkState.Joined)
        {
            // Stand in the host's world (map), like a real player who loaded the same save area.
            if (bot.Session.HostWorld is { World.Length: > 0 } hw && bot.World.LocalInfo.World != hw.World)
                bot.World.LocalInfo = bot.World.LocalInfo with { World = hw.World };
            bot.Tick();
            double now = bot.World.Now;
            if (pw != null && now >= next)
            {
                next = now + 1.0 / 30;
                var actors = new List<ProbeActor>();
                var (pos, yaw, _) = bot.World.Truth(now);
                actors.Add(new ProbeActor($"0x{0x100 + bot.Session.LocalId:X}", pos, 0, yaw, true, false, true));
                foreach (var rp in bot.Session.Remotes.Values)
                    if (rp.Buffer.Sample(now, out var s) && s.Latest.InWorld)
                        actors.Add(new ProbeActor($"0x{0x200 + rp.Id:X}", s.Pos, 0, s.Yaw, true, false, true));
                pw.Write(now, actors);
            }
            Thread.Sleep(5);
        }
        pw?.Dispose();

        var host = bot.Session.Remotes.Values.FirstOrDefault(r => r.Id == PeerIds.Host);
        Console.WriteLine("BOT_SUMMARY {" +
            $"\"state\":\"{bot.Session.State}\",\"id\":{bot.Session.LocalId},\"remotes\":{bot.Session.Remotes.Count}," +
            $"\"host_states\":{host?.StatesReceived ?? 0},\"host_name\":\"{host?.Name}\",\"host_char\":\"{host?.Info?.Character}\"," +
            $"\"host_world\":\"{host?.Info?.World}\",\"rtt_ms\":{(host?.RttMs ?? -1).ToString("0", ci)},\"sent\":{bot.Session.StatesSent}" + "}");
        bot.Session.Leave();
        for (int i = 0; i < 20; i++) { bot.Tick(); Thread.Sleep(10); }
        return 0;
    }
}
