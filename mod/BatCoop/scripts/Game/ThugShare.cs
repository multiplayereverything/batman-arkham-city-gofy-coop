#nullable enable
// Thugs fight every player, not just the host.
//
// Host: a thug in a fight that is nearer a guest's puppet than the host is "on" that guest. The game's AI cannot fight
// a puppet itself (a puppet has no controller: a thug given one as its target drops out of the fight, lab 0.4.1), so
// the host keeps the thug's own AI from swinging at the host and sends its attacks to the guest instead
// (NpcMsg.Attack), one every few seconds, like the game's own attack turns. Puppets take no damage on the host.
//
// Guest: the guest's copy of that thug (frozen AI, driven by the host) plays a real thug close attack on the guest,
// the way PVP's proxies did (PvpBridge.ForceAttack): counter icon, counter window, and the game's own hit if the
// guest does not counter. A counter hits the copy, and that hit goes to the host like any other (ForwardNpcHit).
using System.Numerics;
using BatCoop.Core;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatCoop.Game;

public sealed class ThugShare(Action<string> log)
{
    readonly Action<string> _log = log;

    // ---------- host ----------

    sealed class Assigned
    {
        public byte Player;            // PeerIds.Host = the host itself
        public double NextAttack;
        public bool? CouldAttack;      // the AI's own bCanAttack before the thug went on a guest
    }

    readonly Dictionary<long, Assigned> _assigned = [];
    readonly Dictionary<byte, double> _guestBusyUntil = [];
    readonly Random _rng = new();
    double _nextAssign;
    public long Retargets, AttacksSent;
    public string LastError = "";

    /// <summary>How near (unreal units) a guest has to be for a thug to go for them; 6 m, within a close attack.</summary>
    public float Reach = 600;

    public void HostTick(CoopSession s, PuppetManager puppets, Pawn? host, double now)
    {
        if (host is not RPawnPlayerCombat hostPawn || now < _nextAssign) return;
        _nextAssign = now + 0.25;
        try { Assign(s, puppets, hostPawn, now); }
        catch (Exception e) { LastError = "assign: " + e.Message; }
    }

    void Assign(CoopSession s, PuppetManager puppets, RPawnPlayerCombat host, double now)
    {
        var targets = new List<(byte id, Vector3 at)>();
        foreach (var p in puppets.All.Values) if (p.Pawn.IsValid && !p.Pawn.bDeleteMe) targets.Add((p.Id, p.Pawn.Location));
        var seen = new HashSet<long>();
        foreach (var v in BmSDK.Framework.Game.FindObjects<RPawnVillain>())
        {
            if (!NpcHostPort.IsLive(v) || NpcHostPort.IsDown(v)) continue;
            if (v.Controller is not RBMAIController c || !c.bInCombat) continue;
            long key = v.Ptr.ToInt64();
            _assigned.TryGetValue(key, out var a);
            // Nearest player wins; a guest has to be clearly nearer to take a thug off the host (no flip-flopping).
            float best = Vector3.Distance(v.Location, host.Location) * (a is { Player: not PeerIds.Host } ? 1.15f : 0.85f);
            byte want = PeerIds.Host;
            foreach (var (id, at) in targets)
            {
                float d = Vector3.Distance(v.Location, at);
                if (d < Reach && d < best) { best = d; want = id; }
            }
            if (want == PeerIds.Host && a == null) continue;
            seen.Add(key);
            a ??= _assigned[key] = new Assigned { Player = PeerIds.Host };
            var ai = c.CombatBehaviour as RBMBehaviour_CombatAI;
            if (a.Player != want)
            {
                Retargets++;
                if (want == PeerIds.Host) { Restore(ai, a); _assigned.Remove(key); continue; }
                a.Player = want;
                a.NextAttack = now + 0.5 + _rng.NextDouble();
            }
            // The thug's own AI would swing at the host: it fights the guest instead.
            if (ai != null)
            {
                a.CouldAttack ??= ai.bCanAttack;
                ai.bCanAttack = false;
            }
            if (now < a.NextAttack || now < _guestBusyUntil.GetValueOrDefault(a.Player)) continue;
            uint npcId = s.NpcHost.IdOf(key);
            if (npcId == 0) continue;
            s.NpcHost.SendAttack(a.Player, npcId);
            AttacksSent++;
            a.NextAttack = now + 2.5 + _rng.NextDouble() * 2;
            _guestBusyUntil[a.Player] = now + 1.2; // one thug swings at a time, as in the game's fights
        }
        foreach (var k in _assigned.Keys.Where(k => !seen.Contains(k)).ToList()) _assigned.Remove(k);
    }

    static void Restore(RBMBehaviour_CombatAI? ai, Assigned a)
    {
        if (ai != null && a.CouldAttack is { } could) try { ai.bCanAttack = could; } catch { }
    }

    /// <summary>Host: give every thug back to the host (a guest left, or the feature was turned off).</summary>
    public void HostReset(Pawn? host)
    {
        foreach (var v in BmSDK.Framework.Game.FindObjects<RPawnVillain>())
        {
            if (!_assigned.TryGetValue(v.Ptr.ToInt64(), out var a) || !NpcHostPort.IsLive(v)) continue;
            try { Restore((v.Controller as RBMAIController)?.CombatBehaviour as RBMBehaviour_CombatAI, a); } catch { }
        }
        _assigned.Clear();
        _guestBusyUntil.Clear();
    }

    public int FightingGuests => _assigned.Values.Count(a => a.Player != PeerIds.Host);

    // ---------- guest ----------

    sealed record Defense(uint NpcId, RPawnVillain Thug, double Start, bool Forced);

    readonly List<Defense> _defenses = [];
    public long AttacksPlayed, ForcedAttacks, Countered, HitsTaken, HitsApplied;
    static Class? _strike, _closeAttack;

    /// <summary>When a thug's close attack lands after its counter icon shows (measured in 0.3: about 1.1 s).</summary>
    public const double HitDelay = PvpBridge.HitDelay;
    /// <summary>Damage of one unanswered thug punch when the game's own attack could not be played.</summary>
    public int FallbackDamage = 10;

    public void GuestTick(CoopSession s, NpcGuestBinder binder, RPawnPlayer me, double now)
    {
        while (s.NpcGuest.Attacks.TryDequeue(out var id))
        {
            if (!s.NpcGuest.Mirrors.TryGetValue(id, out var m) || m.Local is not RPawnVillain v || !v.IsValid || NpcHostPort.IsDown(v)) continue;
            if (_defenses.Any(d => d.NpcId == id)) continue;
            AttacksPlayed++;
            bool forced = ForceAttack(v, me, binder.DriveOf(id), now);
            _defenses.Add(new Defense(id, v, now, forced));
        }
        if (_defenses.Count == 0) return;
        RCombatMove? move = null;
        try { move = me.CombatMove; } catch { }
        for (int i = _defenses.Count - 1; i >= 0; i--)
        {
            var d = _defenses[i];
            if (!d.Thug.IsValid || d.Thug.bDeleteMe) { _defenses.RemoveAt(i); continue; }
            if (move is RCombatMove_BatmanCounter c && c.IsValid && c.Target != null && c.Target.Ptr == d.Thug.Ptr)
            {
                // Our counter strikes the copy; that hit goes to the host's thug (ForwardNpcHit).
                _defenses.RemoveAt(i);
                Countered++;
                Release(d.Thug);
                continue;
            }
            double age = now - d.Start;
            RCombatMove? tm = null;
            try { tm = d.Thug.CombatMove; } catch { }
            bool alive = tm is RCombatMove_VillainAttack { IsValid: true };
            if (age >= HitDelay || (d.Forced && !alive && age > 0.25))
            {
                _defenses.RemoveAt(i);
                Land(me, d, tm as RCombatMove_VillainCloseAttack);
            }
        }
    }

    /// <summary>The copy attacks us with a thug's close attack (counter icon and window). Its AI stays frozen: the
    /// combat move plays on the pawn itself.</summary>
    bool ForceAttack(RPawnVillain v, RPawnPlayer me, NpcDrive? drive, double now)
    {
        if (v.Controller is not RBMAIController ctrl) { LastError = "thug copy has no AI controller"; return false; }
        _closeAttack ??= BmSDK.Framework.Game.FindObject<Class>("BmGame.RCombatMove_VillainCloseAttack");
        if (_closeAttack == null) { LastError = "no close attack class"; return false; }
        try
        {
            v.SetTargetPlayer(me);
            if (!ctrl.bInCombat) ctrl.EnterCombat(true, true);
            var beh = ctrl.CombatBehaviour;
            if (beh == null) { LastError = "thug copy has no combat behaviour"; return false; }
            bool wasAllowed = beh is RBMBehaviour_CombatAI ai && ai.bCanAttack;
            if (beh is RBMBehaviour_CombatAI ai1) ai1.bCanAttack = true;
            bool ok = beh.AttackTargetPawnWithClass(_closeAttack);
            if (beh is RBMBehaviour_CombatAI ai2) ai2.bCanAttack = wasAllowed;
            if (!ok) { LastError = "the game refused the attack"; return false; }
            ForcedAttacks++;
            // The host's thug is already next to us: the attack connects from where the copy stands.
            if (v.CombatMove is RCombatMove_VillainCloseAttack close && close.IsValid)
            {
                close.bForceHitTarget = true;
                close.ForceHitDist = 450;
            }
            // The binder must not walk the copy away during its swing.
            if (drive != null) drive.ActionUntil = Math.Max(drive.ActionUntil, now + 1.6);
            return true;
        }
        catch (Exception e) { LastError = "forced attack: " + e.Message; return false; }
    }

    RPawnVillain? _landingFrom;

    void Land(RPawnPlayer me, Defense d, RCombatMove_VillainCloseAttack? close)
    {
        HitsApplied++;
        _landingFrom = d.Thug;
        long before = HitsTaken;
        try
        {
            if (close != null && close.IsValid) close.DamagePawn(me);
            if (HitsTaken == before)
            {
                // The game's attack is gone or dealt nothing: a thug's punch from the copy.
                var dir = me.Location - d.Thug.Location;
                if (dir.LengthSquared() > 1) dir = Vector3.Normalize(dir);
                _strike ??= BmSDK.Framework.Game.FindObject<Class>("BmGame.RDmgType_Strike");
                me.TakeDamage(FallbackDamage, d.Thug.Controller, me.Location, dir * 30000, _strike!, default, d.Thug);
            }
        }
        catch (Exception e) { LastError = "hit: " + e.Message; }
        finally { _landingFrom = null; }
        Release(d.Thug);
    }

    static void Release(RPawnVillain v)
    {
        try { v.ClearCombatMove(); } catch { }
    }

    /// <summary>Damage to our own player. A thug copy's own strike counts as the hit of its open attack; a copy
    /// without an open attack (a stray swing of a replayed animation) does nothing. Returns false to drop it.</summary>
    public bool OnLocalDamage(Pawn? source)
    {
        if (source is not RPawnVillain v) return true;
        if (_landingFrom != null && _landingFrom.Ptr == v.Ptr) { HitsTaken++; return true; }
        int i = _defenses.FindIndex(d => d.Thug.Ptr == v.Ptr);
        if (i < 0) return !IsMirror(v);
        _defenses.RemoveAt(i);
        HitsTaken++;
        return true;
    }

    public Func<Pawn, bool> IsMirror = _ => false;

    public void Forget()
    {
        _defenses.Clear();
        _assigned.Clear();
        _guestBusyUntil.Clear();
    }

    public int OpenDefenses => _defenses.Count;
}
