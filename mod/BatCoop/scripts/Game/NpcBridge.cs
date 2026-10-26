#nullable enable
// The game side of NPC sync. Host: reads every live AI pawn and applies guests' hits. Guest: binds each host NPC to
// a local pawn (the same level-placed pawn, or a freshly spawned one), drives it from the host's state with its AI
// frozen, and freezes and hides its own AI pawns that the host does not have.
using System.Numerics;
using BatCoop.Core;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatCoop.Game;

public sealed class NpcHostPort : INpcHostPort
{
    readonly Dictionary<long, RBMPawnAI> _byHandle = [];

    public static bool IsLive(RBMPawnAI p) =>
        p.IsValid && !p.bDeleteMe && !p.Name.ToString()!.StartsWith("Default__") && p.WorldInfo != null;

    public IEnumerable<NpcSample> Sample()
    {
        _byHandle.Clear();
        foreach (var p in BmSDK.Framework.Game.FindObjects<RBMPawnAI>())
        {
            if (!IsLive(p)) continue;
            long h = p.Ptr.ToInt64();
            _byHandle[h] = p;
            var flags = NpcFlags.None;
            if (p.Health <= 0) flags |= NpcFlags.Down;
            if (p.bHidden) flags |= NpcFlags.Hidden;
            var st = new NpcState
            {
                Pos = p.Location,
                Yaw = SnapshotBuffer.Normalize(p.Rotation.Yaw),
                Vel = p.Velocity,
                Physics = (byte)p.Physics,
                Flags = flags,
                Health = (ushort)Math.Clamp(p.Health, 0, ushort.MaxValue),
            };
            yield return new NpcSample(h, p.GetPathName(null), p.Class.GetPathName(null), p.CharactersType?.GetPathName(null) ?? "", st);
        }
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

public sealed class NpcGuestBinder
{
    readonly Action<string> _log;
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
            if (!m.Buffer.Sample(now, out var s)) continue;
            pawn.SetLocation(s.Pos);
            pawn.SetRotation(new Rotator(0, s.Yaw, 0));
            pawn.Velocity = s.Vel;
            bool down = (s.Latest.Flags & PlayerFlags.Dead) != 0;
            if (down && pawn.Health > 0)
            {
                // Copy the result: the host's NPC is down, so make ours go down the game's own way, once.
                ApplyingLocal = true;
                try { pawn.TakeDamage(pawn.Health + 1000, null, pawn.Location, Vector3.Zero, null, default, null); }
                finally { ApplyingLocal = false; }
            }
            else if (!down) pawn.Health = Math.Max(1, (int)s.Latest.Health);
            bool hidden = (s.Latest.Flags & PlayerFlags.Hidden) != 0;
            if (pawn.bHidden != hidden) pawn.SetHidden(hidden);
        }

        if (now >= _nextScan)
        {
            _nextScan = now + 1;
            ScanOwnPawns(me);
        }
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
