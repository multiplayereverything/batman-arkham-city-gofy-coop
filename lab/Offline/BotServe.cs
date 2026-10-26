using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BatCoop.Core;

namespace BatCoop.Lab;

/// <summary>A bot player driven step by step over a line protocol (like the game's lab bridge), so the in-game lab
/// can play either side against one real game copy:
///   Offline botserve --link-exe X --port 38900 --name LabBot [--x 0 --y 0 --z 0]
/// Commands: host &lt;port&gt; | join &lt;addr&gt; | state | setflag NAME 0|1 | chapter N | npcs_load FILE | hit KEYPART DMG |
///           watch on|off | vote | hits | leave | quit</summary>
public static class BotServe
{
    public static int Run(string[] args)
    {
        string linkExe = "", name = "LabBot";
        int port = 38900, linkPort = 38960;
        var at = Vector3.Zero;
        var ci = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--link-exe": linkExe = args[++i]; break;
                case "--port": port = int.Parse(args[++i]); break;
                case "--link-port": linkPort = int.Parse(args[++i]); break;
                case "--name": name = args[++i]; break;
                case "--x": at.X = float.Parse(args[++i], ci); break;
                case "--y": at.Y = float.Parse(args[++i], ci); break;
                case "--z": at.Z = float.Parse(args[++i], ci); break;
                default: Console.Error.WriteLine("unknown argument " + args[i]); return 2;
            }
        }

        var path = FakeWorld.Circle(at, 200, 150);
        using var bot = new Bot(name, linkPort, new FakeWorld(name, path) { LocalInfo = new(name, CharacterId.Batman, "RPawnPlayerBm", "OW") }, linkExe, ".");
        var story = new FakeStory { Session = bot.Session };
        var npcs = new FakeNpcs();
        bot.Session.WorldPort = story;
        bot.Session.NpcPort = npcs;
        bool watching = false;
        int skips = 0;
        bot.Session.Vote.SkipNow += () => skips++;

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"botserve on 127.0.0.1:{port}");
        var inbox = new System.Collections.Concurrent.ConcurrentQueue<(string line, Action<string> reply)>();
        new Thread(() =>
        {
            while (true)
            {
                var c = listener.AcceptTcpClient();
                new Thread(() =>
                {
                    using var s = c.GetStream();
                    var r = new StreamReader(s);
                    var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    string? line;
                    while ((line = r.ReadLine()) != null)
                    {
                        var done = new ManualResetEventSlim();
                        string ans = "";
                        inbox.Enqueue((line.Trim(), a => { ans = a; done.Set(); }));
                        done.Wait(30000);
                        w.WriteLine(ans);
                    }
                }) { IsBackground = true }.Start();
            }
        }) { IsBackground = true }.Start();

        string J(object o) => JsonSerializer.Serialize(o);
        bool quit = false;
        while (!quit)
        {
            // Stand in the host's world, like a player who loaded the same area.
            if (bot.Session.HostWorld is { World.Length: > 0 } hw && bot.World.LocalInfo.World != hw.World)
                bot.World.LocalInfo = bot.World.LocalInfo with { World = hw.World };
            bot.Session.Vote.LocalWatching(watching, bot.Session.IsHost, bot.Session.LocalId);
            bot.Tick();
            while (inbox.TryDequeue(out var cmd))
            {
                var p = cmd.line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string ans;
                try
                {
                    switch (p[0])
                    {
                        case "host": bot.Session.Host(TransportKind.Direct, 4, int.Parse(p[1])); ans = J(new { ok = true }); break;
                        case "join": bot.Session.Join(TransportKind.Direct, p[1]); ans = J(new { ok = true }); break;
                        case "leave": bot.Session.Leave(); ans = J(new { ok = true }); break;
                        case "setflag": story.ScriptSets(WorldEffect.Flag("g:" + p[1], p.Length < 3 || p[2] != "0")); ans = J(new { ok = true }); break;
                        case "chapter": story.ScriptSets(WorldEffect.Number("ch:main", int.Parse(p[1]))); ans = J(new { ok = true }); break;
                        case "settext": story.ScriptSets(WorldEffect.Text(p[1], cmd.line[(cmd.line.IndexOf(p[1], StringComparison.Ordinal) + p[1].Length)..].Trim())); ans = J(new { ok = true }); break;
                        case "npcs_load":
                            var file = cmd.line[("npcs_load".Length)..].Trim(); // the path can contain spaces
                            foreach (var n in JsonDocument.Parse(File.ReadAllText(file)).RootElement.EnumerateArray())
                            {
                                var pos = n.GetProperty("pos").EnumerateArray().Select(x => x.GetSingle()).ToArray();
                                var npc = npcs.Add(n.GetProperty("key").GetString()!, new Vector3(pos[0], pos[1], pos[2]), 120, 60);
                                npc.PawnClass = n.GetProperty("class").GetString()!;
                                npc.CharacterClass = n.GetProperty("char").GetString() ?? "";
                            }
                            ans = J(new { ok = true, npcs = npcs.All.Count });
                            break;
                        case "hit":
                            var m = bot.Session.NpcGuest.Mirrors.Values.FirstOrDefault(x => x.Info.Key.Contains(p[1], StringComparison.OrdinalIgnoreCase));
                            if (m == null) { ans = J(new { ok = false, error = "no mirror like " + p[1] }); break; }
                            bot.Session.NpcGuest.SendHit(new NpcHit(m.Info.Id, int.Parse(p[2]), m.Latest.Pos, Vector3.Zero, "", 0));
                            ans = J(new { ok = true, npc = m.Info.Key });
                            break;
                        case "watch": watching = p.Length < 2 || p[1] != "off"; ans = J(new { ok = true, watching }); break;
                        case "vote": bot.Session.Vote.LocalVote(bot.Session.IsHost, bot.Session.LocalId); ans = J(new { ok = true }); break;
                        case "hits": ans = J(new { ok = true, hits = npcs.All.Where(n => n.Hits.Count > 0).Select(n => new { n.Key, count = n.Hits.Count, damage = n.Hits.Sum(h => h.Damage), attacker = n.Hits[0].Attacker }) }); break;
                        case "state":
                            var ws = bot.Session.World;
                            var cur = story.Read();
                            ans = J(new
                            {
                                ok = true, state = bot.Session.State.ToString(), status = bot.Session.StatusText, id = bot.Session.LocalId,
                                remotes = bot.Session.Remotes.Values.Select(r => new { r.Id, r.Name, r.StatesReceived, world = r.Info?.World }),
                                world = new { seq = ws.Seq, baseline = ws.HasBaseline, committed = ws.TxnsCommitted, applied = ws.TxnsApplied,
                                              intents_sent = ws.IntentsSent, intents_applied = ws.IntentsApplied, repairs = ws.Repairs,
                                              snapshots = ws.SnapshotsApplied, values = cur?.CountSet ?? -1, hash = cur?.Hash().ToString("X16"),
                                              travels = story.Travels },
                                flags = story.Values.Values.Where(v => v.Kind == WorldValueKind.Flag && v.Int != 0).Select(v => v.Key[2..]).OrderBy(x => x).ToList(),
                                ints = story.Values.Values.Where(v => v.Kind == WorldValueKind.Int).ToDictionary(v => v.Key, v => v.Int),
                                map = story.Values.TryGetValue("lvl:map", out var mp) ? mp.Str : "",
                                npc_mirrors = bot.Session.NpcGuest.Mirrors.Values.Select(x => new { x.Info.Id, x.Info.Key, x.States, down = (x.Latest.Flags & NpcFlags.Down) != 0, health = x.Latest.Health }),
                                npc_shared = bot.Session.NpcHost.SharedCount, npc_hits_applied = bot.Session.NpcHost.HitsApplied,
                                vote = new { votes = bot.Session.Vote.Votes, needed = bot.Session.Vote.Needed, skips },
                            });
                            break;
                        case "quit": quit = true; ans = J(new { ok = true }); break;
                        default: ans = J(new { ok = false, error = "unknown " + p[0] }); break;
                    }
                }
                catch (Exception e) { ans = J(new { ok = false, error = e.Message }); }
                cmd.reply(ans);
            }
            Thread.Sleep(10);
        }
        bot.Session.Leave();
        for (int i = 0; i < 20; i++) { bot.Tick(); Thread.Sleep(10); }
        return 0;
    }
}
