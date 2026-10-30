#nullable enable
// Redirects that keep PVP combat proxies (PvpBridge) under control: they attack only when told to, never throw
// bricks or bark, and freeflow combat can always target them even though they are not drawn.
//
// PVP is a draft since 0.3.2 (PvpBridge.Available): these redirects are compiled only with BATCOOP_PVP, which is
// never defined. Installed, they crashed the game a few seconds into Catwoman's level (CW) at the start of a new
// game, with PVP off and no other player: one of the six (not narrowed down yet) breaks there.
using System.Numerics;
using BmSDK;
using BmSDK.BmGame;

namespace BatCoop.Game;

static class PvpHooks
{
    public static long BlockedAttacks, TargetOverrides;

#if BATCOOP_PVP
    [Redirect(typeof(RBMBehaviour_Combat), nameof(RBMBehaviour_Combat.CanStartAttacking))]
#endif
    static bool CanStartAttacking(RBMBehaviour_Combat self)
    {
        var v = self.Villain;
        if (v != null && PvpBridge.IsProxy(v) && !PvpBridge.MayAttack(v)) { BlockedAttacks++; return false; }
        return self.CanStartAttacking();
    }

#if BATCOOP_PVP
    [Redirect(typeof(RBMBehaviour_CombatAI), nameof(RBMBehaviour_CombatAI.CanStartAttacking))]
#endif
    static bool CanStartAttackingAI(RBMBehaviour_CombatAI self)
    {
        var v = self.Villain;
        if (v != null && PvpBridge.IsProxy(v) && !PvpBridge.MayAttack(v)) { BlockedAttacks++; return false; }
        return self.CanStartAttacking();
    }

#if BATCOOP_PVP
    [Redirect(typeof(RBMBehaviour_CombatAI), nameof(RBMBehaviour_CombatAI.SpawnThrowBrickMove))]
#endif
    static void SpawnThrowBrickMove(RBMBehaviour_CombatAI self)
    {
        if (self.Villain != null && PvpBridge.IsProxy(self.Villain)) { BlockedAttacks++; return; }
        self.SpawnThrowBrickMove();
    }

#if BATCOOP_PVP
    [Redirect(typeof(RCombatMove_VillainAttack), nameof(RCombatMove_VillainAttack.PlayAttackBark))]
#endif
    static void PlayAttackBark(RCombatMove_VillainAttack self, FString barkType)
    {
        if (self.Villain != null && PvpBridge.IsProxy(self.Villain)) return; // an invisible thug must not shout
        self.PlayAttackBark(barkType);
    }

#if BATCOOP_PVP
    [Redirect(typeof(RPawnCombat), nameof(RPawnCombat.CanBeTargettedByPlayer))]
#endif
    static bool CanBeTargettedByPlayer(RPawnCombat self, Class damageType)
    {
        if (PvpBridge.IsProxy(self)) return true;
        return self.CanBeTargettedByPlayer(damageType);
    }

#if BATCOOP_PVP
    [Redirect(typeof(RPlayerControllerCombat), nameof(RPlayerControllerCombat.IsValidStrikeTarget))]
#endif
    static bool IsValidStrikeTarget(RPlayerControllerCombat self, Vector3 facingDir, Class dmgType, RPawnVillain testPawn)
    {
        if (testPawn != null && PvpBridge.IsProxy(testPawn) && self.Pawn != null)
        {
            // Like a thug in reach: close enough and roughly in the direction the player pushes.
            var to = testPawn.Location - self.Pawn.Location;
            float dist = to.Length();
            TargetOverrides++;
            if (dist < 1) return true;
            var flat = Vector3.Normalize(to with { Z = 0 });
            var f = facingDir with { Z = 0 };
            return dist < 700 && (f.LengthSquared() < 0.01f || Vector3.Dot(flat, Vector3.Normalize(f)) > 0.3f);
        }
        return self.IsValidStrikeTarget(facingDir, dmgType, testPawn!);
    }
}
