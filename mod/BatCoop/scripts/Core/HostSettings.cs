#nullable enable
// The host's game settings (the host menu): one record, sent to everyone when it changes and to every newcomer.
// Guests see them read-only.
using System;
using System.Collections.Generic;

namespace BatCoop.Core;

public enum SkipMode : byte { Vote = 0, HostOnly = 1 }

public sealed record HostSettings
{
    /// <summary>Players can fight each other like thugs: strikes, counters, damage.</summary>
    public bool Pvp { get; init; }
    /// <summary>PVP damage in percent of what a thug's hit does (50..200).</summary>
    public int PvpDamagePercent { get; init; } = 100;
    /// <summary>F8 ("go to friend") allowed.</summary>
    public bool AllowTeleport { get; init; } = true;
    /// <summary>Names above other players.</summary>
    public bool NameTags { get; init; } = true;
    public SkipMode CutsceneSkip { get; init; } = SkipMode.Vote;
    /// <summary>Mission, defeat and death messages in the chat.</summary>
    public bool EventFeed { get; init; } = true;

    public byte[] Encode() => new WireWriter().U8((byte)GameMsg.Settings)
        .Bool(Pvp).U16((ushort)PvpDamagePercent).Bool(AllowTeleport).Bool(NameTags).U8((byte)CutsceneSkip).Bool(EventFeed)
        .ToArray();

    public static HostSettings Decode(ref WireReader r) => new()
    {
        Pvp = r.Bool(),
        PvpDamagePercent = Math.Clamp((int)r.U16(), 10, 500),
        AllowTeleport = r.Bool(),
        NameTags = r.Bool(),
        CutsceneSkip = (SkipMode)r.U8(),
        EventFeed = r.Bool(),
    };

    /// <summary>The ini keys the host's choices are kept under between games.</summary>
    public IEnumerable<(string key, string value)> ToIni()
    {
        yield return ("HostPvp", Pvp ? "1" : "0");
        yield return ("HostPvpDamage", PvpDamagePercent.ToString());
        yield return ("HostAllowTeleport", AllowTeleport ? "1" : "0");
        yield return ("HostNameTags", NameTags ? "1" : "0");
        yield return ("HostCutsceneSkip", CutsceneSkip == SkipMode.HostOnly ? "host" : "vote");
        yield return ("HostEventFeed", EventFeed ? "1" : "0");
    }

    public static HostSettings FromIni(CoopConfig c) => new()
    {
        // PVP is off unless the host turned it on.
        Pvp = c.Bool("HostPvp", false),
        PvpDamagePercent = Math.Clamp(c.Int("HostPvpDamage", 100), 10, 500),
        AllowTeleport = c.Bool("HostAllowTeleport", true),
        NameTags = c.Bool("HostNameTags", true),
        CutsceneSkip = c.Str("HostCutsceneSkip", "vote").Equals("host", StringComparison.OrdinalIgnoreCase) ? SkipMode.HostOnly : SkipMode.Vote,
        EventFeed = c.Bool("HostEventFeed", true),
    };
}
