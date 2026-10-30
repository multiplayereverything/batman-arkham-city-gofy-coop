#nullable enable
// Reads the local player's pose description and replays one on a puppet (see Core/AnimSync.cs). The pose calls
// are native: calling them from C# works, only redirects never see them.
using System.Numerics;
using BatCoop.Core;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;
using FCustomAnimConfig = BmSDK.BmGame.RAnimConfig.FCustomAnimConfig;
using FCustomTransitionConfig = BmSDK.BmGame.RPawnCharacter.FCustomTransitionConfig;
using FCustomIdleConfig = BmSDK.BmGame.RPawnCharacter.FCustomIdleConfig;
using EMirrorChoice = BmSDK.BmGame.RAnimUtil.EMirrorChoice;

namespace BatCoop.Game;

static class AnimBridge
{
    public static int TransitionId(RPawnCharacter pawn)
    {
        try { return pawn.Anim?.PosePlayer.LastTransitionId ?? 0; } catch { return 0; }
    }

    static string N(FName n) { var s = n.ToString() ?? ""; return s == "None" ? "" : s; }
    static string P(GameObject? o) => o != null && o.IsValid ? o.GetPathName(null) : "";

    public static PoseCall Read(RPawnCharacter pawn)
    {
        var d = pawn.Pose;
        var type = (PoseTransition)(byte)d.TransitionType;
        var target = d.TransitionTargetPosition;
        float yaw = d.TransitionTargetRotation.Yaw;
        if (type == PoseTransition.Slaved && d.TransitionSlavedTo.Actor is { IsValid: true } a)
        {
            // A transition slaved to another actor (a takedown on a thug): the actor is local to this machine,
            // so send where it stands; the puppet plays the same clip aimed at that spot.
            target = a.Location;
            yaw = a.Rotation.Yaw;
        }
        var ct = d.CustomTransition;
        var ci = d.CustomIdle;
        return new PoseCall(type, N(d.MovementStance), N(d.WeaponStance), N(d.IdleStance), N(d.TransitionName),
            P(ct.CustomFullBodyAnim), N(ct.FullBodyAnim), P(ct.CustomUpperBodyAnim), P(ci.CustomFullBodyAnim),
            d.Mirrored, (byte)d.TransitionMirrored, d.TransitionDurationScale, d.TransitionOverrideDurationEnabled, d.TransitionOverrideDuration,
            target, yaw, d.TransitionMeetingPointBegin, d.TransitionStartTime);
    }

    static AnimSequence? Clip(string path) => path.Length == 0 ? null : BmSDK.Framework.Game.FindObject<AnimSequence>(path);

    static FName F(string s) => new(s.Length == 0 ? "None" : s);

    /// <summary>How long a call's clip plays, in seconds (0 when it has none or it is not loaded).</summary>
    public static float ClipSeconds(PoseCall c)
    {
        var clip = Clip(c.Clip);
        if (clip == null) return 0;
        float rate = clip.RateScale > 0 ? clip.RateScale : 1;
        float scale = c.DurationScale > 0 ? c.DurationScale : 1;
        return c.OverrideDuration && c.Duration > 0 ? c.Duration : clip.SequenceLength / rate * scale;
    }

    /// <summary>Starts the owner's transition on the puppet. Returns false when a clip it needs is not loaded
    /// here (the stance change is still applied).</summary>
    public static bool Replay(RPawnCharacter puppet, PoseCall c, out string why)
    {
        why = "";
        var clip = Clip(c.Clip);
        bool ok = c.Clip.Length == 0 || clip != null;
        if (!ok) why = "clip not loaded: " + c.Clip;

        var anim = new FCustomAnimConfig();
        if (clip != null) anim.CustomFullBodyAnim = clip;
        else if (c.ClipName.Length > 0) anim.FullBodyAnim = F(c.ClipName);
        if (Clip(c.UpperClip) is { } up) anim.CustomUpperBodyAnim = up;
        var ct = new FCustomTransitionConfig { Config = anim };
        var idleAnim = new FCustomAnimConfig();
        if (Clip(c.IdleClip) is { } idle) idleAnim.CustomFullBodyAnim = idle;
        var ci = new FCustomIdleConfig { Config = idleAnim };

        var mv = F(c.Movement);
        var wp = F(c.Weapon);
        var tn = F(c.Transition);
        var mc = (EMirrorChoice)c.MirrorChoice;
        int yawUnits = (int)(c.TargetYaw * 65536f / 360f);
        switch (c.Type)
        {
            case PoseTransition.Relative:
            case PoseTransition.Slaved:
                puppet.StartRelativeTransition(mv, wp, c.TargetPos, yawUnits, c.MeetingBegin, tn, c.Mirrored, ct, ci, mc, c.DurationScale, c.StartTime);
                break;
            case PoseTransition.RelativeRotationOnly:
                puppet.StartRelativeTransitionRotationOnly(mv, wp, yawUnits, c.MeetingBegin, tn, c.Mirrored, ct, ci, mc, c.DurationScale, c.StartTime);
                break;
            default:
                if (c.OverrideDuration) puppet.ForceChangePose(mv, wp, tn, c.Mirrored, ct, ci, mc, c.Duration);
                else puppet.ChangePose(mv, wp, tn, c.Mirrored, ct, ci, mc, c.DurationScale);
                break;
        }
        return ok;
    }

    /// <summary>The clip the pawn's newest transition is playing and how far it is (0..1), for the lab.</summary>
    public static (string clip, float time) Playing(RPawnCharacter pawn)
    {
        try
        {
            var changes = pawn.Anim.PosePlayer.Changes;
            if (changes.Count == 0) return ("", 0);
            var last = changes[changes.Count - 1];
            var seq = last.Transition.Anim.Parts_0;
            return (seq != null && seq.IsValid ? seq.GetPathName(null) : "", last.Time.Current);
        }
        catch { return ("", 0); }
    }
}
