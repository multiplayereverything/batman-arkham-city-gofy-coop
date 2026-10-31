#nullable enable
// The game side of NPC sync. Host: reads every live AI pawn and applies guests' hits. Guest: binds each host NPC to
// a local pawn (the same level-placed pawn, or a freshly spawned one), drives it from the host's state with its AI
// frozen, and freezes and hides its own AI pawns that the host does not have.
//
// A guest's copy is driven like a player puppet (Puppets.cs): it walks to the host's position under the game's own
// walk cycle, plays the host NPC's transitions (strikes, hit reactions, knock-downs), and when the host's NPC is
// knocked out it is knocked out here the game's own way and left lying where it fell.
using System.Numerics;
using BatCoop.Core;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using EMovementSpeed = BmSDK.BmGame.RPoseConfig.EMovementSpeed;

namespace BatCoop.Game;

public sealed class NpcHostPort : INpcHostPort
{
    readonly Dictionary<long, RBMPawnAI> _byHandle = [];

    /// <summary>A real NPC of this world: not a class default, not being deleted, not one of our PVP proxies.</summary>
    public static bool IsLive(RBMPawnAI p) =>
        p.IsValid && !p.bDeleteMe && !p.Name.ToString()!.StartsWith("Default__") && p.WorldInfo != null && !PvpBridge.IsProxy(p);

    /// <summary>Knocked out for good. A thug the player beats keeps some health: the game marks it "good as dead"
    /// (0.3.2 looked only at health, so guests never saw a knock-out).</summary>
    public static bool IsDown(RBMPawnAI p) =>
        p.Health <= 0 || p.bReallyDead || (p is RPawnVillain v && (v.bGoodAsDead || v.bDiedAlready));

    public static bool IsRagdoll(RBMPawnAI p) { try { return p.IsRagdoll(); } catch { return false; } }

    public IEnumerable<NpcSample> Sample()
    {
        _byHandle.Clear();
        foreach (var p in BmSDK.Framework.Game.FindObjects<RBMPawnAI>())
        {
            if (!IsLive(p)) continue;
            long h = p.Ptr.ToInt64();
            _byHandle[h] = p;
            var flags = NpcFlags.None;
            if (IsDown(p)) flags |= NpcFlags.Down;
            if (p.bHidden) flags |= NpcFlags.Hidden;
            if (IsRagdoll(p)) flags |= NpcFlags.Ragdoll;
            var st = new NpcState
            {
                Pos = p.Location,
                Yaw = SnapshotBuffer.Normalize(p.Rotation.Yaw),
                Vel = p.Velocity,
                Physics = (byte)p.Physics,
                Flags = flags,
                Health = (ushort)Math.Clamp(p.Health, 0, ushort.MaxValue),
            };
            yield return new NpcSample(h, p.GetPathName(null), p.Class.GetPathName(null), p.CharactersType?.GetPathName(null) ?? "", st,
                                       AnimBridge.TransitionId(p));
        }
    }

    public PoseCall? ReadPose(long handle)
    {
        if (!_byHandle.TryGetValue(handle, out var p) || !IsLive(p)) return null;
        try { return AnimBridge.Read(p); } catch { return null; }
    }

    public void ApplyHit(long handle, in NpcHit hit)
    {
        if (!_byHandle.TryGetValue(handle, out var p) || !IsLive(p)) return;
        var dmgType = hit.DamageType.Length > 0 ? BmSDK.Framework.Game.FindObject<Class>(hit.DamageType) : null;
        Controller? by = null;
        try { by = BmSDK.Framework.Game.GetPlayerController(); } catch { }
        p.TakeDamage(hit.Damage, by, hit.HitLocation, hit.Momentum, dmgType, default, null);
    }
}

/// <summary>How the guest is driving one bound NPC (the NPC version of a Puppet's fields).</summary>
public sealed class NpcDrive
{
    public bool Kinematic = true, Moving, DownApplied;
    public byte Speed = 255;
    public double ActionUntil, NextFace;
    public long Replayed, ReplayFailures;
    public string LastError = "", DownHow = "";
}

public sealed class NpcGuestBinder
{
    const byte PHYS_Walking = 1;
    readonly Action<string> _log;
    readonly Dictionary<uint, NpcDrive> _drive = [];
    readonly Dictionary<long, NpcMirror> _byPawn = [];         // local pawn address -> mirror it shows
    readonly Dictionary<long, RBMPawnAI> _suppressed = [];      // our own AI pawns kept frozen and hidden
    readonly Dictionary<uint, double> _retry = [];
    double _nextScan;

    public NpcGuestBinder(Action<string> log) => _log = log;

    public float SuppressRadius = 8000;
    /// <summary>True while the binder itself damages a pawn (copying a knock-out), so the hit hook lets it through.</summary>
    public static bool ApplyingLocal;
    public int Bound => _byPawn.Count;
    public int Suppressed => _suppressed.Count;
    public int SpawnFailures;
    public long Knockouts, PosesReplayed;
    /// <summary>Lab: how a knock-out is copied ("strike" = the game's own damage path, "goodasdead", "ragdoll").</summary>
    public static string DownMode = "strike";
    /// <summary>Walk the copies with the game's walk cycle (off: snap to the host's positions, as in 0.3).</summary>
    public bool Animated = true;

    public NpcDrive? DriveOf(uint id) => _drive.GetValueOrDefault(id);

    public NpcMirror? MirrorOf(Pawn p) => _byPawn.GetValueOrDefault(p.Ptr.ToInt64());

    public void Update(NpcGuest guest, Vector3 me, double now)
    {
        foreach (var m in guest.Removed) Release(m);
        guest.Removed.Clear();

        foreach (var m in guest.Mirrors.Values)
        {
            if (m.Local is not RBMPawnAI pawn || !pawn.IsValid || pawn.bDeleteMe)
            {
                m.Local = null;
                if (_retry.TryGetValue(m.Info.Id, out var t) && now < t) continue;
                if (!Bind(m)) { _retry[m.Info.Id] = now + 3; continue; }
                pawn = (RBMPawnAI)m.Local!;
            }
            var d = _drive.TryGetValue(m.Info.Id, out var dd) ? dd : _drive[m.Info.Id] = new NpcDrive();
            try { Drive(pawn, m, d, now); }
            catch (Exception e) { d.LastError = e.GetType().Name + ": " + e.Message; }
        }

        if (now >= _nextScan)
        {
            _nextScan = now + 1;
            ScanOwnPawns(me);
        }
    }

    void Drive(RBMPawnAI pawn, NpcMirror m, NpcDrive d, double now)
    {
        if (!m.Buffer.Sample(now, out var s)) return;
        bool hidden = (s.Latest.Flags & PlayerFlags.Hidden) != 0;
        if (pawn.bHidden != hidden) pawn.SetHidden(hidden);
        bool down = (s.Latest.Flags & PlayerFlags.Dead) != 0;

        // The host NPC's animations first: a knock-down is a transition too, so it plays before the copy goes limp.
        while (m.Poses.TryTake(now, m.Buffer.Delay, out var call))
        {
            bool action = call.Clip.Length > 0 || call.ClipName.Length > 0;
            if (action && d.Moving) { pawn.StopMoveTo(); d.Moving = false; }
            try
            {
                if (!AnimBridge.Replay(pawn, call, out var why)) { d.ReplayFailures++; d.LastError = why; }
                d.Replayed++;
                PosesReplayed++;
                if (action) d.ActionUntil = now + Math.Clamp(AnimBridge.ClipSeconds(call) * (1 - call.StartTime), 0.3f, 3f);
            }
            catch (Exception ex) { d.ReplayFailures++; d.LastError = ex.GetType().Name + ": " + ex.Message; }
        }

        if (down)
        {
            if (!d.DownApplied) KnockOut(pawn, m, d, s);
            // Lying where it fell: only a big difference (the host's body slid away) moves it.
            if (!NpcHostPort.IsRagdoll(pawn) && Vector3.Distance(pawn.Location, s.Pos) > 250) pawn.SetLocation(s.Pos);
            return;
        }
        if (d.DownApplied) return; // the guest's copy cannot get up again; the host's NPC will be dropped or replaced
        pawn.Health = Math.Max(1, (int)s.Latest.Health);

        var err = s.Pos - pawn.Location;
        bool walking = Animated && s.Latest.Physics == PHYS_Walking && !hidden;
        if (!walking || err.Length() > 400 || Math.Abs(err.Z) > 150)
        {
            if (!d.Kinematic) { d.Kinematic = true; d.Moving = false; }
            if (pawn.Physics != Actor.EPhysics.PHYS_None) pawn.SetPhysics(Actor.EPhysics.PHYS_None, false);
            pawn.SetLocation(s.Pos);
            pawn.SetRotation(new Rotator(0, s.Yaw, 0));
            pawn.Velocity = s.Vel;
            return;
        }
        if (d.Kinematic)
        {
            pawn.SetPhysics(Actor.EPhysics.PHYS_Walking, false);
            d.Kinematic = false;
            d.Speed = 255;
            d.Moving = false;
        }
        if (now < d.ActionUntil) return; // a strike or hit reaction moves the pawn itself

        var flatVel = s.Vel with { Z = 0 };
        float speed = flatVel.Length();
        if (speed < 30)
        {
            if (d.Moving) { pawn.StopMoveTo(); d.Moving = false; }
            if (now >= d.NextFace)
            {
                d.NextFace = now + 0.25;
                pawn.FaceAt(pawn.Location + (new Rotator(0, s.Yaw, 0).ToDirection() with { Z = 0 }) * 500);
            }
        }
        else
        {
            var want = speed < 120 ? EMovementSpeed.MS_WalkSlow : speed < 300 ? EMovementSpeed.MS_Walk : speed < 650 ? EMovementSpeed.MS_Run : EMovementSpeed.MS_Sprint;
            if ((byte)want != d.Speed) { pawn.SetMovementSpeed(want, 1); d.Speed = (byte)want; }
            pawn.MoveTo(s.Pos + flatVel * 0.15f);
            d.Moving = true;
        }
        var flatErr = err with { Z = 0 };
        float e = flatErr.Length();
        if (e > 20) pawn.SetLocation(pawn.Location + flatErr * (e > 150 ? 1f : 0.35f));
    }

    /// <summary>The host's NPC is knocked out: knock ours out once, the game's own way, so it falls and stays down
    /// (no AI runs on it, so it never gets up).</summary>
    void KnockOut(RBMPawnAI pawn, NpcMirror m, NpcDrive d, RenderSample s)
    {
        d.DownApplied = true;
        Knockouts++;
        if (d.Moving) { try { pawn.StopMoveTo(); } catch { } d.Moving = false; }
        ApplyingLocal = true;
        try
        {
            switch (DownMode)
            {
                case "pose":
                    break; // only the host's own transitions (its ragdoll stance)
                case "goodasdead":
                    if (pawn is RPawnVillain gv) gv.SetGoodAsDead();
                    break;
                case "ragdoll":
                    if (pawn is RPawnVillain rv) rv.RagdollKill();
                    break;
                default:
                    // The same damage a finishing strike does, pushed the way the host's NPC was moving.
                    if (pawn.Physics == Actor.EPhysics.PHYS_None) pawn.SetPhysics(Actor.EPhysics.PHYS_Walking, false);
                    var strike = BmSDK.Framework.Game.FindObject<Class>("BmGame.RDmgType_Strike");
                    var push = s.Vel.LengthSquared() > 1 ? Vector3.Normalize(s.Vel with { Z = 0 }) : pawn.Rotation.ToDirection() * -1;
                    pawn.TakeDamage(Math.Max(pawn.Health, 1) + 1000, null, pawn.Location, push * 20000, strike, default, null);
                    break;
            }
            d.DownHow = $"{DownMode}: health {pawn.Health}, good as dead {(pawn as RPawnVillain)?.bGoodAsDead}, ragdoll {NpcHostPort.IsRagdoll(pawn)}";
        }
        catch (Exception e) { d.DownHow = $"{DownMode} failed: {e.GetType().Name}: {e.Message}"; }
        finally { ApplyingLocal = false; }
        _log($"npc {m.Info.Id} knocked out on the host: {d.DownHow}");
    }

    bool Bind(NpcMirror m)
    {
        // A level-placed NPC has the same object path on every machine: take that one over.
        var pawn = BmSDK.Framework.Game.FindObject<RBMPawnAI>(m.Info.Key);
        bool spawned = false;
        if (pawn == null || !NpcHostPort.IsLive(pawn) || _byPawn.ContainsKey(pawn.Ptr.ToInt64()))
        {
            var pawnClass = BmSDK.Framework.Game.FindObject<Class>(m.Info.PawnClass);
            var charClass = m.Info.CharacterClass.Length > 0 ? BmSDK.Framework.Game.FindObject<Class>(m.Info.CharacterClass) : null;
            if (pawnClass == null) { SpawnFailures++; _log($"npc {m.Info.Id}: class {m.Info.PawnClass} not loaded here"); return false; }
            try
            {
                pawn = RCharacter.StaticCreatePawn(m.Latest.Pos, new Rotator(0, m.Latest.Yaw, 0), null, pawnClass, charClass, false, FName.None, FName.None) as RBMPawnAI;
            }
            catch (Exception e) { pawn = null; _log($"npc {m.Info.Id}: spawn failed: {e.Message}"); }
            if (pawn == null) { SpawnFailures++; return false; }
            spawned = true;
        }
        _suppressed.Remove(pawn.Ptr.ToInt64());
        Freeze(pawn, hide: false);
        _drive[m.Info.Id] = new NpcDrive();
        m.Local = pawn;
        m.Spawned = spawned;
        _byPawn[pawn.Ptr.ToInt64()] = m;
        return true;
    }

    static void Freeze(RBMPawnAI pawn, bool hide)
    {
        // No local AI: the host decides what this NPC does.
        try { pawn.Controller?.SetTickIsDisabled(true); } catch { }
        pawn.SetPhysics(Actor.EPhysics.PHYS_None, false);
        if (hide)
        {
            pawn.SetHidden(true);
            pawn.SetCollision(false, false, true);
            pawn.SetTickIsDisabled(true);
        }
    }

    void ScanOwnPawns(Vector3 me)
    {
        float r2 = SuppressRadius * SuppressRadius;
        foreach (var p in BmSDK.Framework.Game.FindObjects<RBMPawnAI>())
        {
            if (!NpcHostPort.IsLive(p)) continue;
            long h = p.Ptr.ToInt64();
            if (_byPawn.ContainsKey(h) || _suppressed.ContainsKey(h)) continue;
            if (Vector3.DistanceSquared(p.Location, me) > r2) continue;
            Freeze(p, hide: true);
            _suppressed[h] = p;
        }
    }

    void Release(NpcMirror m)
    {
        _drive.Remove(m.Info.Id);
        if (m.Local is not RBMPawnAI pawn) return;
        _byPawn.Remove(pawn.Ptr.ToInt64());
        m.Local = null;
        if (!pawn.IsValid) return;
        if (m.Spawned) { try { pawn.Destroy(); } catch { } }
        else { Freeze(pawn, hide: true); _suppressed[pawn.Ptr.ToInt64()] = pawn; }
    }

    /// <summary>The world was replaced: every pawn we held belonged to it. Drop them without calling into them.</summary>
    public void ForgetAll(NpcGuest guest)
    {
        foreach (var m in guest.Mirrors.Values) m.Local = null;
        guest.Removed.Clear();
        _byPawn.Clear();
        _suppressed.Clear();
        _retry.Clear();
        _drive.Clear();
    }

    /// <summary>Leaving the room: destroy what we spawned and give our own NPCs back their AI.</summary>
    public void ReleaseAll(NpcGuest guest)
    {
        foreach (var m in guest.Mirrors.Values.Concat(guest.Removed)) Release(m);
        guest.Removed.Clear();
        foreach (var p in _suppressed.Values)
        {
            if (!p.IsValid || p.bDeleteMe) continue;
            try
            {
                p.SetTickIsDisabled(false);
                p.Controller?.SetTickIsDisabled(false);
                p.SetHidden(false);
                p.SetCollision(true, true, false);
                p.SetMovementPhysics();
            }
            catch { }
        }
        _suppressed.Clear();
        _byPawn.Clear();
    }
}
