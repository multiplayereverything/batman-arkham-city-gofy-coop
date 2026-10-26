// Offline lab: no game. Real BatCoopLink.exe processes (direct transport) with bot players that run the
// mod's own CoopSession and SnapshotBuffer. Prints one PASS/FAIL line per check and RESULT at the end.
//
//   Offline.exe <BatCoopLink.exe> <out dir> [--only name,name]
using System.Net.Sockets;
using System.Numerics;
using BatCoop.Core;
using BatCoop.Lab;

if (args.Length > 0 && args[0] == "bot") return BotMain.Run(args[1..]);
if (args.Length > 0 && args[0] == "botserve") return BotServe.Run(args[1..]);
if (args.Length > 0 && args[0] == "steamsmoke") return SteamSmoke.Run(args[1], args.Length > 2 ? args[2] : ".");
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

// ---------------------------------------------------------------- world, story and mission state

(Bot host, Bot[] guests, FakeStory hs, FakeStory[] gs) Room(int guestCount, int max = 4, LagProxy? viaLag = null, int dport = 0)
{
    dport = dport == 0 ? NextPort() : dport;
    var host = MakeBot("Host", FakeWorld.Still(new(0, 0, 100)));
    var hs = new FakeStory { Session = host.Session };
    host.Session.WorldPort = hs;
    host.Session.Host(TransportKind.Direct, max, dport);
    TickUntil([host], () => host.Session.State == LinkState.Hosting, 5);
    var guests = new Bot[guestCount];
    var gstories = new FakeStory[guestCount];
    for (int i = 0; i < guestCount; i++)
    {
        guests[i] = MakeBot($"Guest{i + 1}", FakeWorld.Still(new(200 * (i + 1), 0, 100)));
        gstories[i] = new FakeStory { Session = guests[i].Session };
        guests[i].Session.WorldPort = gstories[i];
        guests[i].Session.Join(TransportKind.Direct, $"127.0.0.1:{viaLag?.Port ?? dport}");
    }
    var all = guests.Prepend(host).ToArray();
    TickUntil(all, () => guests.All(g => g.Session.State == LinkState.Joined && g.Session.World.HasBaseline), 10);
    return (host, guests, hs, gstories);
}

bool Converged(FakeStory a, FakeStory b) => a.Read()!.Hash() == b.Read()!.Hash();

Run("world_story_sync", () =>
{
    var (host, guests, hs, gs) = Room(2);
    var all = guests.Prepend(host).ToArray();
    // The host's story moves: a mission starts, chapters advance, a door flag clears, the objective changes.
    hs.ScriptSets(WorldEffect.Flag("g:Ch3_StartMission", true));
    hs.ScriptSets(WorldEffect.Number("ch:main", 3));
    hs.ScriptSets(WorldEffect.Number("ch:side:7", 2));
    hs.ScriptSets(WorldEffect.Text("obj:primary", "Find the Joker in the Steel Mill"));
    hs.ScriptSets(WorldEffect.Flag("o:OW.TheWorld:PersistentLevel.RDoor_12|Locked", true));
    bool ok = TickUntil(all, () => gs.All(g => Converged(hs, g)), 5);
    hs.ScriptSets(WorldEffect.Flag("o:OW.TheWorld:PersistentLevel.RDoor_12|Locked", false));
    bool cleared = TickUntil(all, () => gs.All(g => Converged(hs, g) && !g.Values["o:OW.TheWorld:PersistentLevel.RDoor_12|Locked"].IsSet), 5);
    Check("world_story_sync", ok && cleared, $"flags, chapters, side mission and objective reached both guests; cleared flag cleared too (host committed {host.Session.World.TxnsCommitted} txns, guest1 applied {guests[0].Session.World.TxnsApplied})");

    // A guest walks into a mission trigger: its script asks the host; the mission starts for everyone, once.
    int hostAppliesBefore = hs.Applies;
    gs[0].ScriptSets(WorldEffect.Flag("g:SideMission_Zsasz_Started", true));
    bool notLocal = !gs[0].Values.ContainsKey("g:SideMission_Zsasz_Started");
    bool everywhere = TickUntil(all, () => hs.Values.ContainsKey("g:SideMission_Zsasz_Started") && gs.All(g => g.Values.TryGetValue("g:SideMission_Zsasz_Started", out var v) && v.IsSet), 5);
    TickFor(all, 1);
    Check("world_guest_intent", notLocal && everywhere && hs.Applies - hostAppliesBefore == 1 && host.Session.World.IntentsApplied == 1,
        $"guest's trigger did not change its own copy first ({notLocal}), host applied it {hs.Applies - hostAppliesBefore}x, every copy has it ({everywhere})");

    // Mission location: the host moves to an interior; guests are told to follow.
    hs.ScriptSets(WorldEffect.Text("lvl:map", "Steel_C1"));
    bool follow = TickUntil(all, () => gs.All(g => g.Travels >= 1 && g.Values["lvl:map"].Str == "Steel_C1"), 5);
    Check("world_level_follow", follow, $"both guests were sent to the host's map Steel_C1 (travels: {string.Join(",", gs.Select(g => g.Travels))})");

    // A guest's own script sets a flag natively (no hook sees it): that is the guest's progress, so the host
    // adopts it and every copy gets it.
    gs[1].Corrupt(WorldEffect.Flag("g:Riddler_Trophy_42_Found", true));
    gs[1].Corrupt(WorldEffect.Number("ch:side:3", 4));
    bool adopted = TickUntil(all, () => hs.Values.TryGetValue("g:Riddler_Trophy_42_Found", out var f) && f.IsSet
                                         && hs.Values["ch:side:3"].Int == 4 && gs.All(g => Converged(hs, g)), 10);
    Check("world_guest_native_progress", adopted, $"a flag and a side-mission step a guest's game set by itself reached the host and the other guest");

    // A guest losing state (its game reloaded and dropped flags, a chapter went backwards) is not progress:
    // the digest finds it and the host repairs it.
    gs[1].Corrupt(WorldEffect.Flag("g:Ch3_StartMission", false));
    gs[1].Corrupt(WorldEffect.Number("ch:main", 1));
    bool healed = TickUntil(all, () => Converged(hs, gs[1]), 15);
    Check("world_digest_repair", healed && host.Session.World.Repairs >= 1 && hs.Values["ch:main"].Int == 3,
        $"guest2's lost state repaired by the host, host unchanged at chapter {hs.Values["ch:main"].Int} ({host.Session.World.Repairs} repair(s))");

    foreach (var b in all) b.Dispose();
});

Run("world_late_join", () =>
{
    int dport = NextPort();
    var (host, guests, hs, gs) = Room(1, 4, null, dport);
    var all = guests.Prepend(host).ToList();
    for (int i = 0; i < 80; i++) hs.ScriptSets(WorldEffect.Flag($"g:Bulk_{i}", true));
    hs.ScriptSets(WorldEffect.Number("ch:main", 5));
    TickFor(all, 1);
    var late = MakeBot("Late", FakeWorld.Still(new(900, 0, 100)));
    var ls = new FakeStory { Session = late.Session };
    late.Session.WorldPort = ls;
    late.Session.Join(TransportKind.Direct, $"127.0.0.1:{dport}");
    all.Add(late);
    bool ok = TickUntil(all, () => late.Session.World.HasBaseline && Converged(hs, ls), 10);
    Check("world_late_join", ok && late.Session.World.SnapshotsApplied >= 1, $"late joiner matches the host after a snapshot ({ls.Read()?.CountSet} values)");
    foreach (var b in all) b.Dispose();
});

Run("world_under_lag", () =>
{
    int dport = NextPort();
    using var proxy = new LagProxy(dport, 90, 35);
    var (host, guests, hs, gs) = Room(1, 4, proxy, dport);
    var all = guests.Prepend(host).ToArray();
    var rng = new Random(3);
    for (int step = 0; step < 200; step++)
    {
        // Host story churns while the guest triggers missions of its own.
        hs.ScriptSets(WorldEffect.Flag($"g:F{rng.Next(40)}", rng.Next(2) == 0));
        if (step % 7 == 0) gs[0].ScriptSets(WorldEffect.Flag($"g:G{rng.Next(10)}", true));
        if (step % 11 == 0) hs.ScriptSets(WorldEffect.Number("ch:main", step / 11));
        foreach (var b in all) b.Tick();
        Thread.Sleep(5);
    }
    // Converged and every intent delivered (an intent for a flag that is already set can land after the copies match).
    bool ok = TickUntil(all, () => Converged(hs, gs[0]) && guests[0].Session.World.IntentsSent == host.Session.World.IntentsApplied, 10);
    Check("world_under_lag", ok,
        $"200 random changes through 90+-35 ms lag converge (intents sent {guests[0].Session.World.IntentsSent}, applied {host.Session.World.IntentsApplied}, repairs {host.Session.World.Repairs})");
    foreach (var b in all) b.Dispose();
});

// ---------------------------------------------------------------- NPCs

Run("npc_sync", () =>
{
    int dport = NextPort();
    var host = MakeBot("Host", FakeWorld.Still(new(0, 0, 100)));
    var npcs = new FakeNpcs();
    var near1 = npcs.Add("OW.TheWorld:PersistentLevel.RPawnVillainThug_3", new(1000, 0, 100));
    var near2 = npcs.Add("OW.TheWorld:PersistentLevel.RPawnVillainThug_7", new(-1500, 500, 100));
    npcs.Add("OW.TheWorld:PersistentLevel.RPawnVillainThug_99", new(90000, 0, 100)); // far from everyone
    host.Session.NpcPort = npcs;
    host.Session.Host(TransportKind.Direct, 4, dport);
    TickUntil([host], () => host.Session.State == LinkState.Hosting, 5);
    var guest = MakeBot("Guest", FakeWorld.Still(new(300, 0, 100)));
    guest.Session.Join(TransportKind.Direct, $"127.0.0.1:{dport}");
    var all = new[] { host, guest };
    var g = guest.Session.NpcGuest;
    bool shared = TickUntil(all, () => g.Mirrors.Count == 2 && g.Mirrors.Values.All(m => m.Buffer.Count > 3), 8);
    Check("npc_shared_in_range", shared && !g.Mirrors.Values.Any(m => m.Info.Key.EndsWith("_99")),
        $"guest mirrors {g.Mirrors.Count} NPCs near players, not the one 900 m away (host shares {host.Session.NpcHost.SharedCount})");

    // The guest's view of each NPC follows the host's NPC (interpolated ~150 ms behind).
    double worst = 0; int n = 0;
    var end = DateTime.UtcNow.AddSeconds(3);
    while (DateTime.UtcNow < end)
    {
        foreach (var b in all) b.Tick();
        double now = guest.World.Now;
        foreach (var m in g.Mirrors.Values)
        {
            if (!m.Buffer.Sample(now, out var s)) continue;
            var src = npcs.All.First(x => x.Key == m.Info.Key);
            double best = double.MaxValue;
            for (double back = 0.05; back <= 0.4; back += 0.01) best = Math.Min(best, Vector3.Distance(npcs.PosOf(src, now - back), s.Pos));
            worst = Math.Max(worst, best); n++;
        }
        Thread.Sleep(16);
    }
    Check("npc_motion", worst < 30, $"guest NPC copies stay on the host's paths (worst {worst:0.0} uu over {n} samples)");

    // A guest's punch is an intent: the host applies it exactly once and the result comes back as state.
    var mirror = g.Mirrors.Values.First(m => m.Info.Key == near1.Key);
    guest.Session.NpcGuest.SendHit(new NpcHit(mirror.Info.Id, 100, mirror.Latest.Pos, new(0, 0, 50), "RDmgType_Punch", 0));
    bool down = TickUntil(all, () => (mirror.Latest.Flags & NpcFlags.Down) != 0, 5);
    TickFor(all, 0.5);
    Check("npc_hit_once", down && near1.Hits.Count == 1 && near1.Hits[0].Attacker == guest.Session.LocalId,
        $"host applied the guest's hit {near1.Hits.Count}x (attacker id {near1.Hits.FirstOrDefault().Attacker}); guest sees the NPC down: {down}");

    // A hit from an old epoch (before the host renumbered its NPCs) must not land on anything.
    // The dangerous case is a number that is valid again in the new epoch (a restarted counter, a reused slot),
    // so the stale hit below names an NPC that exists now; only the epoch tag can tell it is old.
    host.Session.NpcHost.Reset();
    TickUntil(all, () => g.Epoch == host.Session.NpcHost.Epoch && g.Mirrors.Values.Any(m => m.Info.Key == near2.Key), 3);
    uint liveId = g.Mirrors.Values.First(m => m.Info.Key == near2.Key).Info.Id;
    var w = new WireWriter().U8((byte)GameMsg.Npc).U8((byte)NpcMsg.Hit).U32(host.Session.NpcHost.Epoch - 1)
        .U32(liveId).U32(50).F32(0).F32(0).F32(0).F32(0).F32(0).F32(0).Str("x");
    int before = near2.Hits.Count + near1.Hits.Count;
    var stale = new WireReader(w.ToArray()); stale.U8();
    host.Session.NpcHost.Receive(1, ref stale, npcs);
    Check("npc_stale_epoch", near2.Hits.Count + near1.Hits.Count == before, "a hit tagged with the previous epoch changed nothing");

    // Leaving interest: the NPC walks far away on the host, the guest drops its copy.
    near2.Center = new(200000, 0, 100);
    bool gone = TickUntil(all, () => !g.Mirrors.Values.Any(m => m.Info.Key == near2.Key), 5);
    Check("npc_gone", gone && g.Removed.Any(m => m.Info.Key == near2.Key), "guest removed the NPC that left everyone's range");
    host.Dispose(); guest.Dispose();
});

// ---------------------------------------------------------------- cutscene vote skip

Run("cutscene_vote", () =>
{
    int dport = NextPort();
    var host = MakeBot("Host", FakeWorld.Still(default));
    host.Session.Host(TransportKind.Direct, 4, dport);
    TickUntil([host], () => host.Session.State == LinkState.Hosting, 5);
    var g1 = MakeBot("G1", FakeWorld.Still(default));
    var g2 = MakeBot("G2", FakeWorld.Still(default));
    g1.Session.Join(TransportKind.Direct, $"127.0.0.1:{dport}");
    g2.Session.Join(TransportKind.Direct, $"127.0.0.1:{dport}");
    var all = new[] { host, g1, g2 };
    TickUntil(all, () => host.Session.Remotes.Count == 2 && g1.Session.State == LinkState.Joined && g2.Session.State == LinkState.Joined, 8);
    var skips = new int[3];
    for (int i = 0; i < 3; i++) { int k = i; all[i].Session.Vote.SkipNow += () => skips[k]++; }
    void Watch(Bot b, bool on) => b.Session.Vote.LocalWatching(on, b.Session.IsHost, b.Session.LocalId);
    void VoteFor(Bot b) => b.Session.Vote.LocalVote(b.Session.IsHost, b.Session.LocalId);

    // Host and G1 are in the cutscene, G2 is not (still loading): two votes are needed.
    Watch(host, true); Watch(g1, true);
    TickUntil(all, () => g2.Session.Vote.Needed == 2, 3);
    VoteFor(g1); VoteFor(g1);
    bool waiting = TickUntil(all, () => g2.Session.Vote.Votes == 1, 3) && skips.All(s => s == 0);
    VoteFor(host);
    bool skipped = TickUntil(all, () => skips.All(s => s == 1), 3);
    TickFor(all, 0.5);
    // A duplicated skip message (resend, reconnect) must not skip anything again.
    var dup = new WireReader(new byte[] { CutsceneVote.Channel, (byte)VoteMsg.Skip, 1, 0, 0, 0 }); dup.U8();
    g2.Session.Vote.GuestReceive(ref dup);
    Check("cutscene_vote", waiting && skipped && skips.All(s => s == 1),
        $"1 of 2 votes waited ({waiting}); second vote skipped on all three machines exactly once ({string.Join(",", skips)})");

    // Next cutscene: one watcher leaves the game; the remaining watcher's vote is enough.
    Watch(host, false); Watch(g1, false); TickFor(all, 0.3);
    Watch(host, true); Watch(g1, true); TickFor(all, 0.3);
    g1.Kill();
    TickUntil([host, g2], () => host.Session.Remotes.Count == 1, 8);
    VoteFor(host);
    bool alone = TickUntil([host, g2], () => skips[0] == 2 && skips[2] == 2, 3);
    Check("cutscene_vote_leaver", alone, $"after a watcher left, the host's vote alone skipped it (skips {string.Join(",", skips)})");

    // The same player in a new room with a new host: that host's rounds start at 1 again and must still skip.
    g2.Session.Leave();
    TickUntil([host, g2], () => g2.Session.State == LinkState.Idle, 5);
    int dport2 = NextPort();
    var newHost = MakeBot("NewHost", FakeWorld.Still(default));
    newHost.Session.Host(TransportKind.Direct, 4, dport2);
    TickUntil([newHost], () => newHost.Session.State == LinkState.Hosting, 5);
    g2.Session.Join(TransportKind.Direct, $"127.0.0.1:{dport2}");
    TickUntil([newHost, g2], () => g2.Session.State == LinkState.Joined, 5);
    int newSkips = 0;
    newHost.Session.Vote.SkipNow += () => newSkips++;
    int g2Before = skips[2];
    Watch(newHost, true); Watch(g2, true);
    TickUntil([newHost, g2], () => g2.Session.Vote.Needed == 2, 3);
    VoteFor(g2); VoteFor(newHost);
    bool fresh = TickUntil([newHost, g2], () => newSkips == 1 && skips[2] == g2Before + 1, 3);
    Check("cutscene_vote_new_room", fresh, $"in a new room under a new host the first skip still happens on the guest ({skips[2] - g2Before})");
    newHost.Dispose();
    host.Dispose(); g2.Dispose();
});

Console.WriteLine($"RESULT: {(failed == 0 ? "PASS" : "FAIL")} ({passed} passed, {failed} failed{(failed > 0 ? ": " + string.Join(", ", failures) : "")})");
return failed == 0 ? 0 : 1;

static class Refs
{
    // Hex ids for animcheck: a player's own body is 0x100+id, the puppet showing them elsewhere 0x200+id.
    public static string Player(byte id) => $"0x{0x100 + id:X}";
    public static string Puppet(byte id) => $"0x{0x200 + id:X}";
}
