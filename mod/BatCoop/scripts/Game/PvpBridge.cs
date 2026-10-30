#nullable enable
// The game side of PVP (see Core/PvpSync.cs). Each other player gets a hidden thug "combat proxy" standing in their
// puppet: freeflow combat targets it, and the counter icon shows above it. The proxy never acts on its own: its
// AI may attack only when the attacker's machine said that player attacked us, and then it plays a real thug
// attack on us, so we can counter it the way we counter a thug, or take the hit.
using System.Numerics;
using BatCoop.Core;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatCoop.Game;

public sealed class PvpBridge
{
    /// <summary>PVP is a draft: off in every build until its hooks are safe in every level (see PvpHooks). While
    /// false no bridge exists, no proxy is spawned, the host menu has no PVP rows and a host's PVP setting
    /// (from an ini or an older host) is ignored.</summary>
    public const bool Available = false;

    sealed class Proxy
    {
        public required byte Player;
        public required RPawnVillain Pawn;
        public double BusyUntil;   // a forced attack is playing
        public bool InCombat;
        public double ReactingSince = -1; // in a hit reaction, knockdown or stun since
        public double ReplaceAt = -1;     // a countered or knocked-down proxy is replaced by a fresh one
    }

    sealed record Defense(uint AttackId, byte Attacker, Proxy Proxy, double Start);

    readonly Action<string> _log;
    readonly Dictionary<byte, Proxy> _proxies = [];
    static readonly HashSet<long> ProxyPtrs = [];
    readonly List<Defense> _defenses = [];
    IntPtr _lastMove;
    double _retryAt;
    double _upAt = -1;
    double _lastHealth = -1;
    string _lastEnemy = "";
    double _lastEnemyAt = double.NegativeInfinity;
    Class? _hitDamageType;

    /// <summary>The proxy whose AI is allowed to start an attack right now (inside a forced attack call).</summary>
    public static RPawnVillain? Forcing;
    /// <summary>Proxies in the middle of a forced attack: the AI re-checks every frame whether it may attack, so
    /// they stay allowed until the attack is over.</summary>
    static readonly HashSet<long> Attacking = [];
    public static bool MayAttack(RPawnVillain v) => Forcing?.Ptr == v.Ptr || Attacking.Contains(v.Ptr.ToInt64());
    public static PvpBridge? Current;
    public string LastError = "";
    public long ProxiesSpawned, ForcedAttacks, CountersApplied;
    /// <summary>Lab: draw the proxies (to see what they do).</summary>
    public bool LabVisible;

    public void SetLabVisible(bool on)
    {
        LabVisible = on;
        foreach (var p in _proxies.Values) if (p.Pawn.IsValid) Hide(p.Pawn, !on);
    }

    public PvpBridge(Action<string> log) { _log = log; Current = this; }

    public static bool IsProxy(GameObject? p) => p != null && ProxyPtrs.Contains(p.Ptr.ToInt64());
    public int ProxyCount => _proxies.Count;

    // ---------- every frame ----------

    public void Tick(CoopSession s, PuppetManager puppets, double now)
    {
        var me = LocalWorld.TryGetPawn();
        WatchHealth(s, me, now);
        s.Pvp.Tick(now);
        if (!s.InRoom || !s.Pvp.Enabled || me == null)
        {
            if (_proxies.Count > 0) DestroyAll();
            _defenses.Clear();
            return;
        }

        // One proxy per puppet.
        foreach (var id in _proxies.Keys.ToList())
            if (!puppets.All.ContainsKey(id) || !_proxies[id].Pawn.IsValid) Destroy(id);
        foreach (var p in puppets.All.Values)
        {
            if (!p.Pawn.IsValid || p.Pawn.bHidden) continue;
            if (!_proxies.TryGetValue(p.Id, out var proxy))
            {
                if (now < _retryAt) continue;
                proxy = Spawn(p.Id, p.Pawn);
                if (proxy == null) { _retryAt = now + 5; continue; }
                _proxies[p.Id] = proxy;
            }
            if (NeedsReplacing(proxy, now)) { Destroy(p.Id); continue; } // a fresh one next frame
            Follow(proxy, p.Pawn, now);
        }

        DetectOurAttacks(s, me, now);
        PlayIncoming(s, me, now);
        ResolveDefenses(s, me, now);

        if (_upAt > 0 && now >= _upAt)
        {
            _upAt = -1;
            try { me.Health = me.HealthMax; } catch { }
        }
    }

    Proxy? Spawn(byte player, Pawn near)
    {
        var thugClass = BmSDK.Framework.Game.FindObject<Class>("BmGame.RPawnVillainThug");
        var charClass = ThugCharacter();
        if (thugClass == null || charClass == null) { LastError = "no thug class loaded in this map"; return null; }
        RBMPawnAI? ai;
        try
        {
            ai = RCharacter.StaticCreatePawn(near.Location, new Rotator(0, near.Rotation.Yaw, 0), null!, thugClass, charClass, false, FName.None, FName.None);
        }
        catch (Exception e) { LastError = "proxy spawn: " + e.Message; _log(LastError); return null; }
        if (ai is not RPawnVillain v) { LastError = "proxy spawn returned " + (ai?.Class.Name.ToString() ?? "null"); return null; }
        ProxyPtrs.Add(v.Ptr.ToInt64());
        Hide(v, !LabVisible);
        v.bBlockActors = false;
        ProxiesSpawned++;
        _log($"PVP proxy for player {player}: {v.Name} ({charClass.Name})");
        return new Proxy { Player = player, Pawn = v };
    }

    /// <summary>A thug character that is loaded in this map: the open world always has some.</summary>
    static Class? ThugCharacter()
    {
        foreach (var name in new[] { "BmScript.RCharacter_JokerThug1", "BmScript.RCharacter_JokerThug2", "BmScript.RCharacter_Outcast1", "BmScript.RCharacter_JokerThugTwoFace" })
            if (BmSDK.Framework.Game.FindObject<Class>(name) is { } c) return c;
        foreach (var t in BmSDK.Framework.Game.FindObjects<RPawnVillainThug>())
            if (NpcHostPort.IsLive(t) && t.CharactersType != null) return t.CharactersType;
        return null;
    }

    /// <summary>Invisible but still a combat target: the actor stays "visible" to the combat code, every drawn
    /// component is hidden.</summary>
    static void Hide(Actor a, bool hide = true)
    {
        foreach (var c in a.AllComponents)
            if (c is PrimitiveComponent pc && pc.IsValid && !ReferenceEquals(pc, a.CollisionComponent)) pc.SetHidden(hide);
    }

    /// <summary>A countered thug is knocked down, and our strikes knock it about: a proxy in that state cannot
    /// attack (or be countered) properly again, so it is replaced. Health alone does not bring it back.</summary>
    static bool NeedsReplacing(Proxy proxy, double now)
    {
        var v = proxy.Pawn;
        if (proxy.ReplaceAt > 0 && now >= proxy.ReplaceAt) return true;
        if (v.Physics == Actor.EPhysics.PHYS_RigidBody) return true;
        var move = v.CombatMove;
        bool reacting = move != null && move.IsValid && move is not RCombatMove_VillainAttack;
        if (!reacting) { proxy.ReactingSince = -1; return false; }
        if (proxy.ReactingSince < 0) proxy.ReactingSince = now;
        return now - proxy.ReactingSince > 1.5;
    }

    void Follow(Proxy proxy, Pawn puppet, double now)
    {
        var v = proxy.Pawn;
        if (v.Health < v.HealthMax) v.Health = v.HealthMax;
        bool attacking = now < proxy.BusyUntil;
        if (!attacking) Attacking.Remove(v.Ptr.ToInt64());
        if (proxy.InCombat && !attacking)
        {
            proxy.InCombat = false;
            try { (v.Controller as RBMAIController)?.ExitCombat(); } catch { }
        }
        // The proxy always stands exactly in the puppet, during its attack too: the counter icon is drawn over the
        // proxy, so it shows over the other player (a thug's attack lunge would carry it off to one side).
        // SetLocation is refused when the proxy would overlap an actor (the puppet, us), so it collides with no
        // actor; it keeps walking physics on the floor, which its attack needs.
        if (v.bCollideActors) v.SetCollision(false, false, false);
        if (Vector3.DistanceSquared(v.Location, puppet.Location) > 1) v.SetLocation(puppet.Location);
        float yaw = puppet.Rotation.Yaw;
        if (attacking && LocalWorld.TryGetPawn() is { } me)
        {
            // While it attacks, it faces the player it attacks.
            var to = me.Location - v.Location;
            yaw = MathF.Atan2(to.Y, to.X) * 180f / MathF.PI;
        }
        v.SetRotation(new Rotator(0, yaw, 0));
        if (!attacking) v.Velocity = Vector3.Zero;
        // The combat code lets only villains that are on screen finish an attack, and judges that by when they
        // were last drawn. The proxy is never drawn, so it is marked as drawn every frame (it stands in the
        // puppet, which is what the player sees).
        MarkRendered(v);
    }

    static void MarkRendered(Actor a)
    {
        float t;
        try { t = BmSDK.Framework.Game.GetWorldInfo().TimeSeconds; } catch { return; }
        a.LastRenderTime = t;
        foreach (var c in a.AllComponents)
            if (c is PrimitiveComponent pc && pc.IsValid) pc.LastRenderTime = t;
    }

    // ---------- attacker side ----------

    void DetectOurAttacks(CoopSession s, RPawnPlayer me, double now)
    {
        var move = me.CombatMove;
        if (move == null || !move.IsValid) { _lastMove = IntPtr.Zero; return; }
        if (move.Ptr == _lastMove) return;
        _lastMove = move.Ptr;
        if (move is not RCombatMove_BatmanAttack attack || move is RCombatMove_BatmanCounter) return;
        var target = attack.Target;
        if (target == null || !IsProxy(target)) return;
        var proxy = _proxies.Values.FirstOrDefault(p => ReferenceEquals(p.Pawn, target) || p.Pawn.Ptr == target.Ptr);
        if (proxy == null) return;
        var id = s.Pvp.SendAttack(s.LocalId, proxy.Player, move.Class.Name.ToString() ?? "", now);
        if (id != 0) _log($"PVP: we attack {s.NameOf(proxy.Player)} ({move.Class.Name}), attack {id}");
    }

    /// <summary>Attacker side: the defender countered us; take the hit their counter deals.</summary>
    public void OnOutcome(CoopSession s, PvpResult r)
    {
        if (r.Attacker != s.LocalId || r.Outcome != PvpOutcome.Countered) return;
        var me = LocalWorld.TryGetPawn();
        if (me == null || !_proxies.TryGetValue(r.Defender, out var proxy)) return;
        int dmg = Math.Max(1, (int)Math.Round(10 * s.Settings.PvpDamagePercent / 100.0));
        var from = proxy.Pawn.Location;
        var dir = me.Location - from;
        if (dir.LengthSquared() > 1) dir = Vector3.Normalize(dir);
        CountersApplied++;
        _applyingCounter = r.Defender;
        try { me.TakeDamage(dmg, proxy.Pawn.Controller, me.Location, dir * 30000, HitDamageType(), default, proxy.Pawn); }
        catch (Exception e) { LastError = "counter hit: " + e.Message; }
        finally { _applyingCounter = PeerIds.None; }
        _log($"PVP: {s.NameOf(r.Defender)} countered us ({dmg} damage)");
    }
    byte _applyingCounter = PeerIds.None;

    Class? HitDamageType() => _hitDamageType ??= BmSDK.Framework.Game.FindObject<Class>("BmGame.RDmgType_Strike");

    // ---------- defender side ----------

    void PlayIncoming(CoopSession s, RPawnPlayer me, double now)
    {
        while (s.Pvp.TryTakeIncoming(out var a))
        {
            if (s.Pvp.IsDown(s.LocalId, now)) { s.Pvp.Report(new PvpResult(a.Id, a.Attacker, s.LocalId, PvpOutcome.Missed, 0)); continue; }
            if (!_proxies.TryGetValue(a.Attacker, out var proxy) || !proxy.Pawn.IsValid || now < proxy.BusyUntil)
            {
                s.Pvp.Report(new PvpResult(a.Id, a.Attacker, s.LocalId, PvpOutcome.Missed, 0));
                continue;
            }
            if (ForceAttack(proxy, me, now)) _defenses.Add(new Defense(a.Id, a.Attacker, proxy, now));
            else s.Pvp.Report(new PvpResult(a.Id, a.Attacker, s.LocalId, PvpOutcome.Missed, 0));
        }
    }

    /// <summary>Our copy of the attacker attacks us with a thug's close attack: counter icon, counter window,
    /// and the game's own hit if we do not counter.</summary>
    bool ForceAttack(Proxy proxy, RPawnPlayer me, double now)
    {
        var v = proxy.Pawn;
        if (v.Controller is not RBMAIController ctrl) { LastError = "proxy has no AI controller"; return false; }
        var cls = BmSDK.Framework.Game.FindObject<Class>("BmGame.RCombatMove_VillainCloseAttack");
        if (cls == null) return false;
        try
        {
            if (!proxy.InCombat) { ctrl.EnterCombat(true, true); proxy.InCombat = true; }
            var beh = ctrl.CombatBehaviour;
            if (beh == null) { LastError = "proxy has no combat behaviour"; return false; }
            Forcing = v;
            if (beh is RBMBehaviour_CombatAI cai) cai.bCanAttack = true;
            bool ok = beh.AttackTargetPawnWithClass(cls);
            if (beh is RBMBehaviour_CombatAI cai2) cai2.bCanAttack = false;
            Forcing = null;
            if (ok)
            {
                proxy.BusyUntil = now + 2.5;
                Attacking.Add(v.Ptr.ToInt64());
                ForcedAttacks++;
                // The proxy stays in the attacker's puppet instead of stepping in, so the attack must connect from
                // there (the attacker's own strike already brought them next to us).
                if (v.CombatMove is RCombatMove_VillainCloseAttack close && close.IsValid)
                {
                    close.bForceHitTarget = true;
                    close.ForceHitDist = 450;
                }
            }
            return ok;
        }
        catch (Exception e) { Forcing = null; LastError = "forced attack: " + e.Message; return false; }
    }

    void ResolveDefenses(CoopSession s, RPawnPlayer me, double now)
    {
        if (_defenses.Count == 0) return;
        var move = me.CombatMove;
        for (int i = _defenses.Count - 1; i >= 0; i--)
        {
            var d = _defenses[i];
            bool countered = move is RCombatMove_BatmanCounter c && c.IsValid && c.Target != null && c.Target.Ptr == d.Proxy.Pawn.Ptr;
            if (countered)
            {
                _defenses.RemoveAt(i);
                s.Pvp.Report(new PvpResult(d.AttackId, d.Attacker, s.LocalId, PvpOutcome.Countered, 0));
                _log($"PVP: we countered {s.NameOf(d.Attacker)}");
                // Let our counter animation finish on it, then put a fresh proxy in.
                d.Proxy.ReplaceAt = now + 1.6;
                d.Proxy.BusyUntil = now + 1.6;
                continue;
            }
            // Not countered: the hit lands when a thug's punch would (the attack's own DamagePawn, so damage,
            // hit reaction and sound are the game's). The thug's own strike contact never fires on an undrawn
            // proxy, and the game gives the attack up early; then the hit lands at that moment.
            var pm = d.Proxy.Pawn.IsValid ? d.Proxy.Pawn.CombatMove : null;
            bool attackAlive = pm is RCombatMove_VillainAttack { IsValid: true };
            double age = now - d.Start;
            if ((attackAlive && age >= HitDelay) || (!attackAlive && age > 0.25))
            {
                ApplyHit(s, me, d, pm as RCombatMove_VillainCloseAttack);
                if (_defenses.Remove(d))
                    s.Pvp.Report(new PvpResult(d.AttackId, d.Attacker, s.LocalId, PvpOutcome.Missed, 0)); // no damage got through (we were down, or immune)
            }
            else if (age > 3.5)
            {
                _defenses.RemoveAt(i);
                s.Pvp.Report(new PvpResult(d.AttackId, d.Attacker, s.LocalId, PvpOutcome.Missed, 0));
            }
        }
    }

    /// <summary>When a thug's close attack lands after its counter icon shows (measured in game: about 1.1 s).</summary>
    public const double HitDelay = 1.15;
    public long HitsApplied;

    /// <summary>Lands a proxy's attack on us. The damage goes through the TakeDamage redirect, which reports it
    /// and removes the defense.</summary>
    void ApplyHit(CoopSession s, RPawnPlayer me, Defense d, RCombatMove_VillainCloseAttack? close)
    {
        HitsApplied++;
        try
        {
            if (close != null && close.IsValid) close.DamagePawn(me);
            if (_defenses.Contains(d))
            {
                // The attack is gone (or did not deal damage): hit like a thug's punch from the proxy.
                var proxy = d.Proxy.Pawn;
                var dir = me.Location - proxy.Location;
                if (dir.LengthSquared() > 1) dir = Vector3.Normalize(dir);
                me.TakeDamage(10, proxy.Controller, me.Location, dir * 30000, HitDamageType(), default, proxy);
            }
        }
        catch (Exception e) { LastError = "hit: " + e.Message; }
        try { d.Proxy.Pawn.ClearCombatMove(); } catch { }
        d.Proxy.BusyUntil = Math.Min(d.Proxy.BusyUntil, (BatCoopScript.Instance?.Now ?? 0) + 0.5);
    }

    // ---------- damage (called from the TakeDamage redirects) ----------

    /// <summary>Returns the damage to apply (0 = none). Proxies never take damage; a PVP hit on us is scaled,
    /// reported and never kills: it defeats.</summary>
    public int FilterDamage(CoopSession? s, Pawn self, int damage, Controller? by, Actor? causer, Class? damageType)
    {
        if (IsProxy(self)) return 0;
        var me = LocalWorld.TryGetPawn();
        if (s == null || me == null || self.Ptr != me.Ptr) return damage;
        var srcPawn = by?.Pawn ?? causer as Pawn;
        bool fromProxy = srcPawn != null && IsProxy(srcPawn);
        if (_applyingCounter != PeerIds.None) return Lethal(s, me, damage, _applyingCounter);
        if (!fromProxy)
        {
            if (srcPawn != null) { _lastEnemy = EnemyName(srcPawn); _lastEnemyAt = NowOf(s); }
            return damage;
        }
        var proxy = _proxies.Values.FirstOrDefault(p => p.Pawn.Ptr == srcPawn!.Ptr);
        if (proxy == null) return 0;
        if (s.Pvp.IsDown(s.LocalId, NowOf(s))) return 0;
        // The damage type of a real thug hit: a counter we take later reuses it (same hit reaction).
        if (damageType != null) _hitDamageType = damageType;
        int scaled = Math.Max(1, (int)Math.Round(damage * s.Settings.PvpDamagePercent / 100.0));
        // Only an attack that is still open can hit: once we countered it (or it timed out), its forced hit is void.
        var d = _defenses.FirstOrDefault(x => ReferenceEquals(x.Proxy, proxy));
        if (d == null) return 0;
        _defenses.Remove(d);
        s.Pvp.Report(new PvpResult(d.AttackId, d.Attacker, s.LocalId, PvpOutcome.Hit, (ushort)scaled));
        return Lethal(s, me, scaled, proxy.Player);
    }

    static double NowOf(CoopSession s) => BatCoopScript.Instance?.Now ?? 0;

    int Lethal(CoopSession s, RPawnPlayer me, int damage, byte by)
    {
        if (damage < me.Health) return damage;
        // Brought down by a player: no game over. Stay down for a moment, then get up with full health.
        double now = NowOf(s);
        s.Pvp.ReportDefeated(s.LocalId, by, now);
        s.SendEvent(FeedKind.Defeated, s.NameOf(by), s.NameOf(s.LocalId));
        _upAt = now + s.Pvp.DownSeconds;
        _log($"PVP: defeated by {s.NameOf(by)}");
        return Math.Max(0, me.Health - 1);
    }

    static string EnemyName(Pawn p)
    {
        string raw = p is RBMPawnAI rc && rc.CharactersType != null ? rc.CharactersType.Name.ToString() ?? "" : p.Class.Name.ToString() ?? "";
        raw = raw.Replace("RCharacter_", "").Replace("RPawnVillain", "").Replace("RPawn", "");
        var h = FeedMessages.Humanize(raw);
        return h.Length > 0 ? "a " + h : "enemies";
    }

    /// <summary>Our player went down to 0 health (not in PVP): tell everyone who did it.</summary>
    void WatchHealth(CoopSession s, RPawnPlayer? me, double now)
    {
        if (me == null) { _lastHealth = -1; return; }
        int hp = me.Health;
        if (_lastHealth > 0 && hp <= 0 && s.InRoom)
        {
            var who = now - _lastEnemyAt < 10 && _lastEnemy.Length > 0 ? _lastEnemy : "enemies";
            s.SendEvent(FeedKind.DiedToEnemy, s.NameOf(s.LocalId), who);
        }
        _lastHealth = hp;
    }

    // ---------- cleanup ----------

    public void Destroy(byte id)
    {
        if (!_proxies.Remove(id, out var p)) return;
        ProxyPtrs.Remove(p.Pawn.Ptr.ToInt64());
        Attacking.Remove(p.Pawn.Ptr.ToInt64());
        try
        {
            if (p.Pawn.IsValid)
            {
                var c = p.Pawn.Controller;
                p.Pawn.Destroy();
                if (c != null && c.IsValid) c.Destroy();
            }
        }
        catch { }
    }

    public void DestroyAll()
    {
        foreach (var id in _proxies.Keys.ToList()) Destroy(id);
        _defenses.Clear();
    }

    /// <summary>The world is gone: forget the proxies without calling into them.</summary>
    public void Forget()
    {
        foreach (var p in _proxies.Values) ProxyPtrs.Remove(p.Pawn.Ptr.ToInt64());
        Attacking.Clear();
        _proxies.Clear();
        _defenses.Clear();
    }

    public object LabState(CoopSession s) => new LabBridge.RawJson(LabBridge.Json(
        ("enabled", s.Pvp.Enabled), ("proxies", _proxies.Count), ("spawned", ProxiesSpawned), ("forced", ForcedAttacks),
        ("sent", s.Pvp.AttacksSent), ("received", s.Pvp.AttacksReceived), ("hits", s.Pvp.Hits), ("counters", s.Pvp.Counters),
        ("misses", s.Pvp.Misses), ("defeats", s.Pvp.Defeats), ("counters_applied", CountersApplied), ("pending", s.Pvp.Pending),
        ("defending", _defenses.Count), ("error", LastError),
        ("proxy_list", _proxies.Values.Select(p => new LabBridge.RawJson(LabBridge.Json(("player", (int)p.Player), ("pawn", p.Pawn.IsValid ? p.Pawn.GetPathName(null) : ""),
            ("pos", p.Pawn.IsValid ? p.Pawn.Location : (object?)null), ("hp", p.Pawn.IsValid ? p.Pawn.Health : 0), ("move", p.Pawn.IsValid && p.Pawn.CombatMove != null ? p.Pawn.CombatMove.Class.Name.ToString() : "")))).ToList())));
}
