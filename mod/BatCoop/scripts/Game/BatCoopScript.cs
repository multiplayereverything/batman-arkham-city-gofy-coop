#nullable enable
// BatCoop entry point: hotkeys, link process, session pump, puppets, HUD and the lab bridge.
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using System.Numerics;
using BatCoop.Core;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatCoop.Game;

[Script]
public sealed class BatCoopScript : Script
{
    internal static BatCoopScript? Instance;

    CoopConfig _cfg = new();
    CoopSession? _session;
    readonly LocalWorld _world = new();
    PuppetManager? _puppets;
    LabBridge? _bridge;
    ProbeWriter? _probe;
    double _nextProbe;
    Process? _linkProcess;
    Action? _pendingAction;
    double _linkWaitUntil;
    double _nextLinkTry;
    bool _hudVisible = true;
    bool _labEnterWorld;
    double _labEnterAt = -1;
    (Vector3 center, float radius, double start, double end)? _labWalk;
    readonly List<(double until, string text)> _toasts = [];
    StreamWriter? _log;

    TransportKind Transport => _cfg.Str("Transport", "steam").Equals("direct", StringComparison.OrdinalIgnoreCase) ? TransportKind.Direct : TransportKind.Steam;
    int LinkPort => _cfg.Int("LinkPort", Versions.DefaultLinkPort);

    // ---------- lifecycle ----------

    public override void Main() => Start();
    public override void OnLoad() => Start();

    void Start()
    {
        if (Instance == this && _session != null) return;
        Instance = this;
        var dir = Mod.DirectoryPath;
        var ini = Path.Combine(dir, "BatCoop.ini");
        if (!File.Exists(ini)) File.WriteAllText(ini, CoopConfig.Template + Environment.NewLine);
        _cfg = CoopConfig.Load(ini);

        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        try { _log = new StreamWriter(Path.Combine(dir, "logs", "batcoop.log"), append: false) { AutoFlush = true }; } catch { }

        _world.PlayerName = _cfg.Str("Name", "");
        _session = new CoopSession(Log);
        _session.Chat += (rp, text) => Toast($"{rp.Name}: {text}");
        _puppets = new PuppetManager(Log) { MirrorPhysics = _cfg.Bool("PuppetMirrorPhysics", false) };
        if (_cfg.Str("Puppet", "player").Equals("none", StringComparison.OrdinalIgnoreCase)) _puppets.Kind = PuppetKind.None;

        int labPort = _cfg.Int("LabPort", 0);
        if (labPort > 0)
        {
            try { _bridge = new LabBridge(labPort); Log($"lab bridge on 127.0.0.1:{_bridge.Port}"); }
            catch (Exception e) { Log($"lab bridge failed: {e.Message}"); }
        }
        _labEnterWorld = _cfg.Bool("LabEnterWorld", false);
        var probePath = _cfg.Str("ProbePath", "");
        if (probePath.Length > 0) _probe = new ProbeWriter(probePath, _cfg.Str("ProbeMachine", "game"));

        Log($"BatCoop {Versions.Mod} ({Versions.Protocol}) loaded; transport {Transport}; F5 host, F6 join, F7 leave");
    }

    public override void OnUnload()
    {
        // Hot reload or shutdown: leave nothing behind in the world.
        _puppets?.DestroyAll();
        _session?.Dispose();
        _bridge?.Dispose();
        _probe?.Dispose();
        _log?.Dispose();
        _session = null;
        if (Instance == this) Instance = null;
    }

    void Log(string s)
    {
        Debug.Log("[BatCoop] " + s, skipSender: true);
        try { _log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); } catch { }
    }

    void Toast(string text, double seconds = 6)
    {
        _toasts.Add((_world.Now + seconds, text));
        if (_toasts.Count > 5) _toasts.RemoveAt(0);
    }

    public override void OnEnterMenu()
    {
        if (_labEnterWorld && _labEnterAt < 0) _labEnterAt = _world.Now + 3;
    }

    public override void OnEnterGame()
    {
        Log($"entered world '{SafeMapName()}'");
        _puppets?.DestroyAll(); // actors from the previous world are gone with it
    }

    static string SafeMapName()
    {
        try { return BmSDK.Framework.Game.GetWorldInfo().GetMapName(false).ToString(); } catch { return ""; }
    }

    // ---------- link process ----------

    /// <summary>Runs <paramref name="action"/> once the link is up, starting BatCoopLink.exe if needed.</summary>
    void WithLink(Action action)
    {
        if (_session == null) return;
        if (_session.LinkUp) { action(); return; }
        _pendingAction = action;
        _linkWaitUntil = _world.Now + 15;
        _nextLinkTry = 0;
        var c = LinkClient.TryConnect(LinkPort, 200);
        if (c != null) { AttachLink(c); return; }
        StartLinkProcess();
    }

    void StartLinkProcess()
    {
        var exe = Path.Combine(Mod.DirectoryPath, "link", "BatCoopLink.exe");
        if (!File.Exists(exe)) { Toast("BatCoopLink.exe is missing from BmGame\\Mods\\BatCoop\\link"); _pendingAction = null; return; }
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(LinkPort.ToString());
        psi.ArgumentList.Add("--parent"); psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("--log"); psi.ArgumentList.Add(Path.Combine(Mod.DirectoryPath, "logs", "link.log"));
        if (Transport == TransportKind.Steam) psi.ArgumentList.Add("--steam");
        // Steam started the game as AppID 200260; the link must run as Spacewar (480).
        psi.Environment.Remove("SteamAppId");
        psi.Environment.Remove("SteamGameId");
        psi.Environment.Remove("SteamOverlayGameId");
        try
        {
            _linkProcess = Process.Start(psi);
            Log($"started BatCoopLink pid {_linkProcess?.Id}");
            Toast("Starting BatCoop network link...");
        }
        catch (Exception e)
        {
            Toast("Could not start BatCoopLink: " + e.Message);
            _pendingAction = null;
        }
    }

    void AttachLink(LinkClient c)
    {
        var name = _cfg.Str("Name", "");
        _session!.Attach(c, name);
        Log("link attached");
        var a = _pendingAction;
        _pendingAction = null;
        a?.Invoke();
    }

    void PumpLinkStart()
    {
        if (_pendingAction == null || _session == null || _session.LinkUp) return;
        double now = _world.Now;
        if (now > _linkWaitUntil) { Toast("BatCoopLink did not start (see BmGame\\Mods\\BatCoop\\logs\\link.log)"); _pendingAction = null; return; }
        if (now < _nextLinkTry) return;
        _nextLinkTry = now + 0.5;
        var c = LinkClient.TryConnect(LinkPort, 100);
        if (c != null) AttachLink(c);
    }

    // ---------- actions ----------

    void Host(TransportKind t) => WithLink(() =>
    {
        _session!.Host(t, Math.Clamp(_cfg.Int("MaxPlayers", 4), 2, 8), _cfg.Int("DirectPort", Versions.DefaultDirectPort));
        Toast(t == TransportKind.Steam ? "Hosting on Steam: friends press F6 to join" : $"Hosting on port {_cfg.Int("DirectPort", Versions.DefaultDirectPort)}");
    });

    void Join(TransportKind t, string target) => WithLink(() =>
    {
        _session!.Join(t, target);
    });

    string JoinTarget()
    {
        if (Transport == TransportKind.Direct) return _cfg.Str("DirectAddress", "127.0.0.1") + ":" + _cfg.Int("DirectPort", Versions.DefaultDirectPort);
        // Steam: a lobby id on the clipboard wins; otherwise find a friend who is hosting.
        var clip = "";
        try { clip = Native.GetClipboard().Trim(); } catch { }
        return ulong.TryParse(clip, out var id) && id > 100000000000000000UL ? clip : "";
    }

    void TeleportToFriend()
    {
        var pawn = LocalWorld.TryGetPawn();
        if (pawn == null || _session == null) return;
        RemotePlayer? best = null;
        float bestD = float.MaxValue;
        foreach (var rp in _session.Remotes.Values)
        {
            if (!rp.Buffer.HasData || !rp.Buffer.Newest.InWorld) continue;
            var d = Vector3.Distance(rp.Buffer.Newest.Pos, pawn.Location);
            if (d < bestD) { bestD = d; best = rp; }
        }
        if (best == null) { Toast("Nobody to go to"); return; }
        var s = best.Buffer.Newest;
        var back = new Rotator(0, s.Yaw, 0).ToDirection() with { Z = 0 };
        pawn.SetLocation(s.Pos - back * 150 + new Vector3(0, 0, 20));
        Toast($"Moved to {best.Name}");
    }

    public override void OnKeyDown(Keys key)
    {
        if (_session == null) return;
        switch (key)
        {
            case Keys.F5: Host(Transport); break;
            case Keys.F6: Join(Transport, JoinTarget()); break;
            case Keys.F7: _session.Leave(); _puppets?.DestroyAll(); Toast("Left the game"); break;
            case Keys.F8: TeleportToFriend(); break;
            case Keys.F9: _hudVisible = !_hudVisible; break;
            case Keys.F10:
                if (_session.IsHost && _session.Lobby.Length > 0)
                {
                    try { Native.SetClipboard(_session.Lobby); } catch { }
                    _session.Invite();
                    Toast($"Copied {_session.Lobby} to the clipboard: friends copy it and press F6");
                }
                break;
        }
    }

    // ---------- tick ----------

    public override void OnTick()
    {
        if (_session == null) return;
        try
        {
            PumpLinkStart();
            _world.Refresh();
            _session.LocalWorld = _world.WorldName;
            LabTick();
            _session.Update(_world);
            _puppets?.Update(_session, _world.WorldName, _world.Now);
            ProbeTick();
            ServeBridge();
        }
        catch (Exception e)
        {
            Log("tick error: " + e);
        }
    }

    void LabTick()
    {
        double now = _world.Now;
        if (_labEnterAt > 0 && now >= _labEnterAt)
        {
            _labEnterAt = 0;
            EnterTestWorld();
        }
        if (_labWalk is { } w)
        {
            var pawn = LocalWorld.TryGetPawn();
            if (pawn == null) return;
            if (now > w.end) { _labWalk = null; pawn.Velocity = Vector3.Zero; return; }
            // Walk a circle at 300 uu/s: position, facing and velocity agree, like real movement.
            double speed = 300;
            double ang = (now - w.start) * speed / w.radius;
            var pos = w.center + new Vector3((float)Math.Cos(ang) * w.radius, (float)Math.Sin(ang) * w.radius, 0);
            var dir = new Vector3(-(float)Math.Sin(ang), (float)Math.Cos(ang), 0);
            pawn.SetLocation(pos);
            pawn.SetRotation(Rotator.FromDirection(dir));
            pawn.Velocity = dir * (float)speed;
        }
    }

    /// <summary>Same entry as BmSDK's DemoScript: the open world on the temporary save slot, so no real
    /// save is touched.</summary>
    void EnterTestWorld()
    {
        Log("lab: entering the open world on the temp save slot");
        var saveMan = BmSDK.Framework.Game.GetGameRI().SaveGameManager;
        saveMan.SetActiveStorySlotID(RSaveGameManager.StorySlots.StorySlot_Temp);
        BmSDK.Framework.Game.GetConsole().ConsoleCommand(
            "start batentry?Players=Playable_Batman?" +
            "Area=OW,OW_A9,OW_A6,OW_A7,OW_A8,OW_R1,OW_R2,OW_E3,OW_E4,OW_A1_Static_LOD,OW_A2,OW_A3_Static_LOD,OW_A4_Static_LOD,OW_A5_Static_LOD,OW_R3,OW_E2_Static_LOD,OW_E6_Static_LOD,OW_E5_Static_LOD,OW_RE1_Static_LOD?" +
            "Flags=Vertical_Slice,Demo_Riddler_Door_Switch,Map_TriggeredCityStories,Batman_ResonatorCodes,Teleport_Church_To_Museum,Demo_Ryder_Bully,Demo_Courthouse_Lock,Public_Demo?" +
            "Chapters=1,1b,2,2a,Z1,V1?" +
            "Start=BeginVS?");
    }

    void ProbeTick()
    {
        if (_probe == null || _session == null || _puppets == null) return;
        double now = _world.Now;
        if (now < _nextProbe) return;
        _nextProbe = now + 1.0 / 30;
        var actors = new List<ProbeActor>();
        if (_world.TryReadLocal(out var me))
            actors.Add(new ProbeActor($"P{_session.LocalId}", me.Pos, me.Pitch, me.Yaw, true, (me.Flags & PlayerFlags.Dead) != 0, true));
        foreach (var p in _puppets.All.Values)
        {
            if (!p.HasSample || !p.Pawn.IsValid) continue;
            var loc = p.Pawn.Location;
            var rot = p.Pawn.Rotation;
            actors.Add(new ProbeActor($"PUP{p.Id}", loc, 0, SnapshotBuffer.Normalize(rot.Yaw), true, false, !p.Pawn.bHidden));
        }
        _probe.Write(now, actors);
    }

    // ---------- HUD ----------

    [Redirect(typeof(RHUD), nameof(RHUD.PostRender))]
    static void PostRenderRedirect(RHUD self)
    {
        self.PostRender();
        try { Instance?.DrawHud(self); } catch { }
    }

    void DrawHud(HUD hud)
    {
        if (!_hudVisible || _session == null) return;
        var c = hud.Canvas;
        if (c == null || !c.IsValid) return;
        float x = 24, y = 24, line = 18;

        void Text(string s, byte r, byte g, byte b)
        {
            c.SetPos(x, y, 0);
            c.SetDrawColor(r, g, b, 255);
            c.DrawText(s, false, 1, 1, out _);
            y += line;
        }

        var head = _session.InRoom ? (_session.IsHost ? "HOST" : $"PLAYER {_session.LocalId + 1}") : _session.State.ToString().ToUpperInvariant();
        Text($"BatCoop {Versions.Mod}  [{head}]  {_session.StatusText}", 255, 220, 90);
        if (!_session.InRoom) Text("F5 Host   F6 Join   F7 Leave   F8 Go to friend   F10 Invite   F9 Hide", 200, 200, 200);

        var pawn = LocalWorld.TryGetPawn();
        foreach (var rp in _session.Remotes.Values)
        {
            string where = "";
            if (rp.Buffer.HasData && pawn != null && rp.Buffer.Newest.InWorld)
                where = $"{Vector3.Distance(rp.Buffer.Newest.Pos, pawn.Location) / 100f:0} m";
            else if (rp.Buffer.HasData && !rp.Buffer.Newest.InWorld) where = "in menus";
            if (rp.Info != null && rp.Info.World.Length > 0 && _world.WorldName.Length > 0 && !rp.Info.World.Equals(_world.WorldName, StringComparison.OrdinalIgnoreCase))
                where = $"in {rp.Info.World}";
            var ping = rp.RttMs >= 0 ? $"{rp.RttMs:0} ms" : "";
            Text($"  {rp.Name}  {rp.Info?.Character}  {where}  {ping}", 150, 210, 255);
        }

        double now = _world.Now;
        _toasts.RemoveAll(t => t.until < now);
        foreach (var t in _toasts) Text(t.text, 255, 255, 255);
        foreach (var n in _session.Notices) Toast(n);
        _session.Notices.Clear();
    }

    // ---------- lab bridge ----------

    void ServeBridge()
    {
        if (_bridge == null) return;
        while (_bridge.TryTake(out var req))
        {
            string answer;
            try { answer = Command(req.Line); }
            catch (Exception e) { answer = LabBridge.Json(("ok", false), ("error", e.GetType().Name + ": " + e.Message)); }
            req.Reply(answer);
        }
    }

    string Command(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var s = _session!;
        string Ok(params (string, object?)[] more) => LabBridge.Json([("ok", true), .. more]);
        float F(int i) => float.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
        TransportKind T(int i) => parts.Length > i && parts[i] == "direct" ? TransportKind.Direct : TransportKind.Steam;

        switch (parts[0])
        {
            case "ping": return Ok();
            case "state": return StateJson();
            case "enterworld": EnterTestWorld(); return Ok();
            case "host": Host(T(1)); return Ok();
            case "join": Join(T(1), parts.Length > 2 ? parts[2] : ""); return Ok();
            case "leave": s.Leave(); _puppets!.DestroyAll(); return Ok();
            case "chat": s.SendChat(line[5..]); return Ok();
            case "exec":
                var res = BmSDK.Framework.Game.GetPlayerController().ConsoleCommand(line[5..], false);
                return Ok(("result", res.ToString()));
            case "tp":
                var pawn = LocalWorld.TryGetPawn() ?? throw new InvalidOperationException("no pawn");
                return Ok(("moved", pawn.SetLocation(new Vector3(F(1), F(2), F(3)))));
            case "walk":
                var p = LocalWorld.TryGetPawn() ?? throw new InvalidOperationException("no pawn");
                float radius = parts.Length > 1 ? F(1) : 400;
                double secs = parts.Length > 2 ? F(2) : 10;
                _labWalk = (p.Location - new Vector3(radius, 0, 0), radius, _world.Now, _world.Now + secs);
                return Ok();
            case "puppets":
                _puppets!.Kind = parts.Length > 1 && parts[1] == "none" ? PuppetKind.None : PuppetKind.Player;
                _puppets.MirrorPhysics = parts.Length > 2 && parts[2] == "mirror";
                _puppets.DestroyAll();
                return Ok(("kind", _puppets.Kind.ToString()), ("mirror", _puppets.MirrorPhysics));
            case "probe":
                _probe?.Dispose();
                _probe = parts.Length > 2 ? new ProbeWriter(parts[1], parts[2]) : null;
                return Ok(("probing", _probe != null));
            default:
                return LabBridge.Json(("ok", false), ("error", "unknown command " + parts[0]));
        }
    }

    string StateJson()
    {
        var s = _session!;
        var pawn = LocalWorld.TryGetPawn();
        var remotes = s.Remotes.Values.Select(rp => new LabBridge.RawJson(LabBridge.Json(
            ("id", (int)rp.Id), ("name", rp.Name), ("character", rp.Info?.Character.ToString()),
            ("world", rp.Info?.World), ("states", rp.StatesReceived), ("rtt_ms", rp.RttMs),
            ("pos", rp.Buffer.HasData ? rp.Buffer.Newest.Pos : (object?)null),
            ("in_world", rp.Buffer.HasData && rp.Buffer.Newest.InWorld)))).ToList();
        var puppets = _puppets!.All.Values.Select(p => new LabBridge.RawJson(LabBridge.Json(
            ("id", (int)p.Id), ("valid", p.Pawn.IsValid), ("class", p.Pawn.IsValid ? p.Pawn.Class.Name.ToString() : ""),
            ("pos", p.Pawn.IsValid ? p.Pawn.Location : (object?)null),
            ("yaw", p.Pawn.IsValid ? SnapshotBuffer.Normalize(p.Pawn.Rotation.Yaw) : 0f),
            ("physics", p.Pawn.IsValid ? p.Pawn.Physics.ToString() : ""),
            ("hidden", p.Pawn.IsValid && p.Pawn.bHidden)))).ToList();
        return LabBridge.Json(
            ("ok", true), ("map", _world.WorldName), ("time", _world.Now),
            ("link", s.LinkUp), ("state", s.State.ToString()), ("status", s.StatusText), ("lobby", s.Lobby),
            ("local_id", (int)s.LocalId), ("states_sent", s.StatesSent),
            ("pawn", pawn != null ? pawn.Class.Name.ToString() : null),
            ("pos", pawn != null ? pawn.Location : (object?)null),
            ("yaw", pawn != null ? SnapshotBuffer.Normalize(pawn.Rotation.Yaw) : 0f),
            ("physics", pawn?.Physics.ToString()),
            ("remotes", remotes), ("puppets", puppets), ("puppet_error", _puppets.LastError));
    }
}
