// Offline lab: no game. Real BatCoopLink.exe processes (direct transport) with bot players that run the
// mod's own CoopSession and SnapshotBuffer. Prints one PASS/FAIL line per check and RESULT at the end.
//
//   Offline.exe <BatCoopLink.exe> <out dir> [--only name,name]
using System.Net.Sockets;
using System.Numerics;
using BatCoop.Core;
using BatCoop.Lab;

if (args.Length < 2) { Console.Error.WriteLine("usage: Offline <BatCoopLink.exe> <out dir> [--only a,b]"); return 2; }
var linkExe = Path.GetFullPath(args[0]);
var outDir = Path.GetFullPath(args[1]);
var only = args.Length > 3 && args[2] == "--only" ? args[3].Split(',').ToHashSet() : null;
Directory.CreateDirectory(outDir);

int passed = 0, failed = 0;
var failures = new List<string>();
int portBase = 37960;

void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}: {detail}");
    if (ok) passed++; else { failed++; failures.Add(name); }
}

void Run(string name, Action body)
{
    if (only != null && !only.Contains(name)) return;
    try { body(); }
    catch (Exception e) { Check(name, false, "exception: " + e.Message); }
}

int NextPort() => Interlocked.Add(ref portBase, 10);

bool TickUntil(IEnumerable<Bot> bots, Func<bool> cond, double seconds)
{
    var list = bots.ToList();
    var end = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < end)
    {
        foreach (var b in list) b.Tick();
        if (cond()) return true;
        Thread.Sleep(16);
    }
    return false;
}

void TickFor(IEnumerable<Bot> bots, double seconds) => TickUntil(bots, () => false, seconds);

Bot MakeBot(string name, Func<double, (Vector3, float, Vector3)> path) =>
    new(name, NextPort(), new FakeWorld(name, path), linkExe, outDir);

// ---------------------------------------------------------------- pure checks

Run("wire_roundtrip", () =>
{
    var st = new PlayerState { Seq = 77, Time = 12.5, Pos = new(1, -2, 3.5f), Yaw = -170, Pitch = 10, Vel = new(300, 0, -5), Physics = 2, Flags = PlayerFlags.InWorld | PlayerFlags.Gliding, Health = 80, HealthMax = 100 };
    var bytes = st.Encode();
    var r = new WireReader(bytes);
    bool kind = r.U8() == (byte)GameMsg.PlayerState;
    var back = PlayerState.Decode(ref r);
    var info = new PlayerInfo("Nick", CharacterId.Catwoman, "RPawnPlayerCatwoman", "batentry");
    var r2 = new WireReader(info.Encode()); r2.U8();
    var info2 = PlayerInfo.Decode(ref r2);
    bool ok = kind && back.Seq == 77 && back.Pos == st.Pos && back.Yaw == -170 && back.Flags == st.Flags && back.Health == 80 && info2 == info && r.Remaining == 0;
    bool truncated = false;
    try { var r3 = new WireReader(bytes.AsSpan(0, 10)); r3.U8(); PlayerState.Decode(ref r3); } catch (WireException) { truncated = true; }
    Check("wire_roundtrip", ok && truncated, $"state, info and truncated-message rejection ({bytes.Length} bytes per state)");
});

Run("interp_jitter_loss", () =>
{
    // 20 Hz sender walking a circle at 600 uu/s; 0-60 ms jitter, 10% loss, out-of-order delivery.
    var path = FakeWorld.Circle(Vector3.Zero, 500, 600);
    var buf = new SnapshotBuffer();
    var rng = new Random(7);
    var arrivals = new List<(double at, PlayerState s)>();
    uint seq = 0;
    for (double t = 0; t < 10; t += 0.05)
    {
        if (rng.NextDouble() < 0.10) { seq++; continue; }
        var (pos, yaw, vel) = path(t);
        arrivals.Add((t + 0.04 + rng.NextDouble() * 0.06, new PlayerState { Seq = ++seq, Time = t, Pos = pos, Yaw = yaw, Vel = vel, Flags = PlayerFlags.InWorld }));
    }
    arrivals.Sort((a, b) => a.at.CompareTo(b.at));
    int ai = 0;
    double maxErr = 0, sumErr = 0; int n = 0;
    for (double now = 0.5; now < 10; now += 1.0 / 60)
    {
        while (ai < arrivals.Count && arrivals[ai].at <= now) { buf.Add(arrivals[ai].s, arrivals[ai].at); ai++; }
        if (!buf.Sample(now, out var smp)) continue;
        // The buffer renders ~Delay behind the sender plus the smallest transit time seen (40 ms here).
        var (truth, _, _) = path(now - buf.Delay - 0.04);
        double err = Vector3.Distance(truth, smp.Pos);
        maxErr = Math.Max(maxErr, err); sumErr += err; n++;
    }
    Check("interp_jitter_loss", maxErr < 40 && sumErr / n < 10, $"mean error {sumErr / n:0.0} uu, max {maxErr:0.0} uu over {n} frames (moving 600 uu/s)");
});

Run("interp_teleport_snap", () =>
{
    var buf = new SnapshotBuffer();
    buf.Add(new PlayerState { Seq = 1, Time = 0, Pos = Vector3.Zero, Flags = PlayerFlags.InWorld }, 0.01);
    buf.Add(new PlayerState { Seq = 2, Time = 0.05, Pos = new(10000, 0, 0), Flags = PlayerFlags.InWorld }, 0.06);
    buf.Sample(0.01 + 0.025 + buf.Delay, out var s);
    bool dup = !buf.Add(new PlayerState { Seq = 2, Time = 0.05, Pos = new(10000, 0, 0) }, 0.07);
    Check("interp_teleport_snap", s.Snapped && s.Pos.X == 10000 && dup, $"midway sample snapped to {s.Pos.X:0} (no 5000 uu half-way smear); duplicate dropped={dup}");
});

// ---------------------------------------------------------------- link + bots

Run("three_players_direct", () =>
{
    int dport = NextPort();
    using var host = MakeBot("Host", FakeWorld.Circle(new(0, 0, 100), 300, 300));
    using var g1 = MakeBot("Guest1", FakeWorld.Circle(new(2000, 0, 100), 300, 300));
    using var g2 = MakeBot("Guest2", FakeWorld.Still(new(0, 2000, 100)));
    var all = new[] { host, g1, g2 };
    host.Session.Host(TransportKind.Direct, 4, dport);
    bool hosting = TickUntil(all, () => host.Session.State == LinkState.Hosting, 5);
    g1.Session.Join(TransportKind.Direct, $"127.0.0.1:{dport}");
    g2.Session.Join(TransportKind.Direct, $"127.0.0.1:{dport}");
    bool joined = TickUntil(all, () => all.All(b => b.Session.Remotes.Count == 2 && b.Session.Remotes.Values.All(r => r.Buffer.HasData && r.Info != null)), 8);
    var ids = all.Select(b => b.Session.LocalId).ToList();
    Check("three_players_direct", hosting && joined && ids.Distinct().Count() == 3 && ids[0] == 0,
        $"hosting={hosting} everyone sees 2 others with data={joined} ids={string.Join(",", ids)}");

    // Positions: each receiver's newest state of each sender is close to that sender's truth now.
    long before = g2.Session.Remotes[g1.Session.LocalId].StatesReceived;
    var t0 = host.World.Now;
    TickFor(all, 2);
    double rate = (g2.Session.Remotes[g1.Session.LocalId].StatesReceived - before) / (host.World.Now - t0);
    double worst = 0;
    foreach (var rx in all)
        foreach (var rp in rx.Session.Remotes.Values)
        {
            var sender = all.First(b => b.Session.LocalId == rp.Id);
            var (truth, _, _) = sender.World.Truth(rp.Buffer.Newest.Time);
            worst = Math.Max(worst, Vector3.Distance(truth, rp.Buffer.Newest.Pos));
        }
    var g1SeenByG2 = g2.Session.Remotes[g1.Session.LocalId];
    Check("states_flow", worst < 0.5, $"every received state matches its sender's path (worst {worst:0.000} uu)");
    Check("guest_to_guest", rate > 18 && g1SeenByG2.Name == "Guest1",
        $"Guest2 gets Guest1's states through the host at {rate:0.0}/s (target 20), name '{g1SeenByG2.Name}'");
    var rtt = host.Session.Remotes.Values.Select(r => r.RttMs).ToList();
    Check("ping_rtt", rtt.All(x => x >= 0 && x < 200), $"host measured round trips {string.Join(", ", rtt.Select(x => $"{x:0} ms"))}");

    string? chatSeen = null;
    g2.Session.Chat += (rp, text) => chatSeen = $"{rp.Name}: {text}";
    g1.Session.SendChat("hello from the rooftop");
    TickUntil(all, () => chatSeen != null, 3);
    Check("chat", chatSeen == "Guest1: hello from the rooftop", $"Guest2 saw '{chatSeen}'");

    // Guest leaves: everyone else is told, quickly.
    g2.Kill();
    bool told = TickUntil([host, g1], () => host.Session.Remotes.Count == 1 && g1.Session.Remotes.Count == 1, 8);
    Check("guest_leaves", told, $"host and Guest1 dropped Guest2 (remaining: host {host.Session.Remotes.Count}, guest1 {g1.Session.Remotes.Count})");

    // Host leaves: the guest's room ends with a reason.
    host.Kill();
    bool ended = TickUntil([g1], () => g1.Session.State == LinkState.Ended, 8);
    Check("host_leaves", ended && g1.Session.Remotes.Count == 0, $"Guest1 state {g1.Session.State}: '{g1.Session.StatusText}'");
});

Run("room_full", () =>
{
    int dport = NextPort();
    using var host = MakeBot("Host", FakeWorld.Still(Vector3.Zero));
    using var a = MakeBot("A", FakeWorld.Still(Vector3.Zero));
    using var b = MakeBot("B", FakeWorld.Still(Vector3.Zero));
    var all = new[] { host, a, b };
    host.Session.Host(TransportKind.Direct, 2, dport);
    TickUntil(all, () => host.Session.State == LinkState.Hosting, 5);
    a.Session.Join(TransportKind.Direct, $"127.0.0.1:{dport}");
    TickUntil(all, () => a.Session.State == LinkState.Joined, 5);
    b.Session.Join(TransportKind.Direct, $"127.0.0.1:{dport}");
    bool refused = TickUntil(all, () => b.Session.State == LinkState.Ended, 5);
    Check("room_full", refused && b.Session.StatusText.Contains("full") && a.Session.State == LinkState.Joined,
        $"third player of a 2-player room: {b.Session.State} '{b.Session.StatusText}'");
});

Run("version_mismatch", () =>
{
    int dport = NextPort();
    using var host = MakeBot("Host", FakeWorld.Still(Vector3.Zero));
    host.Session.Host(TransportKind.Direct, 4, dport);
    TickUntil([host], () => host.Session.State == LinkState.Hosting, 5);
    // An old client: speaks the hub protocol but announces another version.
    using var tcp = new TcpClient("127.0.0.1", dport);
    var s = tcp.GetStream();
    Framing.Write(s, new WireWriter().U8((byte)HubMsg.JoinReq).Str("batcoop-0").Str("Old").ToArray());
    string reason = "";
    var reader = Task.Run(() =>
    {
        while (true)
        {
            var f = Framing.Read(s);
            if (f == null) return;
            var r = new WireReader(f);
            if ((HubMsg)r.U8() == HubMsg.JoinDeny) { reason = r.Str(); return; }
        }
    });
    TickUntil([host], () => reader.IsCompleted, 5);
    Check("version_mismatch", reason.Contains("Version mismatch") && host.Session.Remotes.Count == 0, $"refused with '{reason}'");
});

Run("bad_address", () =>
{
    using var g = MakeBot("Lost", FakeWorld.Still(Vector3.Zero));
    g.Session.Join(TransportKind.Direct, "127.0.0.1:1");
    bool ended = TickUntil([g], () => g.Session.State == LinkState.Ended, 8);
    Check("bad_address", ended && g.Session.StatusText.Contains("cannot reach"), $"{g.Session.State}: '{g.Session.StatusText}'");
});

// ---------------------------------------------------------------- lag + animcheck probe streams

Run("lag_probe", () =>
{
    int dport = NextPort();
    using var host = MakeBot("Host", FakeWorld.Circle(new(0, 0, 100), 600, 450));
    using var guest = MakeBot("Guest", FakeWorld.Circle(new(3000, 0, 100), 400, 300, 1.0f));
    using var proxy = new LagProxy(dport, 90, 35);
    var all = new[] { host, guest };
    host.Session.Host(TransportKind.Direct, 4, dport);
    TickUntil(all, () => host.Session.State == LinkState.Hosting, 5);
    guest.Session.Join(TransportKind.Direct, $"127.0.0.1:{proxy.Port}");
    bool ok = TickUntil(all, () => guest.Session.Remotes.Count == 1 && host.Session.Remotes.Count == 1
                                    && guest.Session.Remotes.Values.All(r => r.Buffer.Count > 3), 10);
    if (!ok) { Check("lag_probe", false, "guest did not join through the lag proxy"); return; }

    // Each machine writes: its own player (owner) and the puppet it renders for the other (observer).
    using var hp = new ProbeWriter(Path.Combine(outDir, "lag.host.probe.jsonl"), "host");
    using var gp = new ProbeWriter(Path.Combine(outDir, "lag.guest.probe.jsonl"), "guest");
    double next = 0, end = host.World.Now + 15;
    while (host.World.Now < end)
    {
        foreach (var b in all) b.Tick();
        double now = host.World.Now;
        if (now >= next)
        {
            next = now + 1.0 / 30;
            foreach (var (bot, w) in new[] { (host, hp), (guest, gp) })
            {
                var actors = new List<ProbeActor>();
                var (pos, yaw, _) = bot.World.Truth(now);
                actors.Add(new ProbeActor(Refs.Player(bot.Session.LocalId), pos, 0, yaw, true, false, true));
                foreach (var rp in bot.Session.Remotes.Values)
                    if (rp.Buffer.Sample(now, out var smp))
                        actors.Add(new ProbeActor(Refs.Puppet(rp.Id), smp.Pos, 0, smp.Yaw, true, false, true));
                w.Write(now, actors);
            }
        }
        Thread.Sleep(5);
    }
    var rtt = guest.Session.Remotes.Values.First().RttMs;
    Check("lag_probe", rtt > 120, $"15 s of probes written through 90+-35 ms each way (guest measured RTT {rtt:0} ms); animcheck judges them");
});

Console.WriteLine($"RESULT: {(failed == 0 ? "PASS" : "FAIL")} ({passed} passed, {failed} failed{(failed > 0 ? ": " + string.Join(", ", failures) : "")})");
return failed == 0 ? 0 : 1;

static class Refs
{
    // Hex ids for animcheck: a player's own body is 0x100+id, the puppet showing them elsewhere 0x200+id.
    public static string Player(byte id) => $"0x{0x100 + id:X}";
    public static string Puppet(byte id) => $"0x{0x200 + id:X}";
}
