#nullable enable
// Redirects that keep one authority:
// - story flags: on a guest, a local script's flag write becomes an intent for the host (the host's next
//   transaction brings it back to everyone, so a mission a guest triggers starts for the whole party);
// - NPC hits: on a guest, damage to an NPC the host owns becomes a hit intent;
// - cutscene skip: in a co-op game, pressing skip is a vote.
using System.Numerics;
using BatCoop.Core;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatCoop.Game;

static class SyncHooks
{
    static BatCoopScript? S => BatCoopScript.Instance;

    /// <summary>How often each hook fired (lab evidence that the game's own scripts go through them).</summary>
    public static readonly Dictionary<string, long> Calls = [];
    static void Count(string hook, string detail)
    {
        Calls[hook] = Calls.GetValueOrDefault(hook) + 1;
        if (Calls[hook] <= 20) S?.HookLog($"{hook}: {detail}");
    }

    [Redirect(typeof(RFlagManager), nameof(RFlagManager.SetGlobalFlag))]
    static bool SetGlobalFlag(RFlagManager self, FString name, bool value)
    {
        var s = S;
        Count("SetGlobalFlag", $"{name}={value}");
        if (s != null && s.GateWorldWrites)
        {
            s.WorldIntent(WorldEffect.Flag("g:" + name, value));
            return true;
        }
        var r = self.SetGlobalFlag(name, value);
        if (s != null) s.World.GlobalFlags[name.ToString() ?? ""] = value;
        return r;
    }

    [Redirect(typeof(RFlagManager), nameof(RFlagManager.SetGlobalFlags))]
    static int SetGlobalFlags(RFlagManager self, FString list)
    {
        var s = S;
        Count("SetGlobalFlags", list.ToString() ?? "");
        if (s != null && s.GateWorldWrites)
        {
            s.WorldIntent(WorldEffect.Text("cmd:setflags", list.ToString() ?? ""));
            return 0;
        }
        var r = self.SetGlobalFlags(list);
        // The list format is the start URL's: names separated by commas. Read each back to record what is set now.
        if (s != null)
            foreach (var n in (list.ToString() ?? "").Split(','))
                if (n.Trim().Length > 0) s.World.GlobalFlags[n.Trim()] = self.GetGlobalFlag(n.Trim());
        return r;
    }

    [Redirect(typeof(RFlagManager), nameof(RFlagManager.SetObjectFlag))]
    static bool SetObjectFlag(RFlagManager self, GameObject obj, FString name, bool value)
    {
        var s = S;
        Count("SetObjectFlag", $"{name}={value}");
        string key = obj != null && obj.IsValid ? obj.GetPathName(null) + "|" + name : "";
        if (s != null && key.Length > 0 && s.GateWorldWrites)
        {
            s.WorldIntent(WorldEffect.Flag("o:" + key, value));
            return true;
        }
        var r = self.SetObjectFlag(obj!, name, value);
        if (s != null && key.Length > 0) s.World.ObjectFlags[key] = value; // the engine has no list to read these from
        return r;
    }

    [Redirect(typeof(RPawn), nameof(RPawn.TakeDamage))]
    static void TakeDamagePawn(RPawn self, int damage, Controller instigatedBy, Vector3 hitLocation, Vector3 momentum,
                               Class damageType, Actor.FTraceHitInfo hitInfo, Actor damageCauser)
    {
        Count("TakeDamage", $"{self.Class.Name} {damage}");
        if (S?.ForwardNpcHit(self, damage, hitLocation, momentum, damageType) == true) return;
        self.TakeDamage(damage, instigatedBy, hitLocation, momentum, damageType, hitInfo, damageCauser);
    }

    [Redirect(typeof(RPawnVillain), nameof(RPawnVillain.TakeDamage))]
    static void TakeDamageVillain(RPawnVillain self, int damage, Controller instigatedBy, Vector3 hitLocation, Vector3 momentum,
                                  Class damageType, Actor.FTraceHitInfo hitInfo, Actor damageCauser)
    {
        Count("TakeDamageVillain", $"{self.Class.Name} {damage}");
        if (S?.ForwardNpcHit(self, damage, hitLocation, momentum, damageType) == true) return;
        self.TakeDamage(damage, instigatedBy, hitLocation, momentum, damageType, hitInfo, damageCauser);
    }

    // No pause in multiplayer: refuse the pause itself. While paused the world does not tick, so a guard that runs
    // in the world tick could never undo it.
    [Redirect(typeof(PlayerController), nameof(PlayerController.SetPause))]
    static bool SetPause(PlayerController self, bool pause, IntPtr canUnpause)
    {
        Count("SetPause", pause.ToString());
        if (pause && S?.BlockPause == true) { S.PausesBlocked++; return false; }
        return self.SetPause(pause, canUnpause);
    }

    [Redirect(typeof(RPlayerController), nameof(RPlayerController.SkipCutscene))]
    static void SkipCutscene(RPlayerController self)
    {
        Count("SkipCutscene", "");
        var s = S;
        if (s != null && s.VoteInsteadOfSkip) { s.VoteSkip(); return; }
        self.SkipCutscene();
    }
}
