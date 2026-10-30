#nullable enable
// BatCoop.ini next to mod.toml. Plain key=value lines; '#' and ';' start comments. Lab runs override keys
// with BATCOOP_<KEY> environment variables, so a lab launch never edits the player's file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BatCoop.Core;

public sealed class CoopConfig
{
    readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CoopConfig Load(string path)
    {
        var c = new CoopConfig();
        if (File.Exists(path))
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '#' or ';' or '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                c._values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var k = (string)e.Key;
            if (k.StartsWith("BATCOOP_", StringComparison.OrdinalIgnoreCase))
                c._values[k["BATCOOP_".Length..]] = (string?)e.Value ?? "";
        }
        return c;
    }

    /// <summary>Sets one key in the ini, keeping every other line (and comments) as it is.</summary>
    public static void SetInFile(string path, string key, string value)
    {
        var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
        bool done = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            int eq = t.IndexOf('=');
            if (eq > 0 && t[0] is not ('#' or ';') && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                done = true;
                break;
            }
        }
        if (!done) lines.Add($"{key}={value}");
        File.WriteAllLines(path, lines);
    }

    public string Str(string key, string def) => _values.TryGetValue(key, out var v) && v.Length > 0 ? v : def;

    public int Int(string key, int def) =>
        _values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : def;

    public float Float(string key, float def) =>
        _values.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;

    public bool Bool(string key, bool def) =>
        _values.TryGetValue(key, out var v) ? v is "1" or "true" or "True" or "yes" or "on" : def;

    public const string Template = """
        # BatCoop settings. Delete a line to use the default.

        # Your name as other players see it. Empty = your Steam name (when Steam is running).
        Name=

        # steam  = Steam lobbies through Spacewar (AppID 480); friends join by invite or with F6.
        # direct = connect by IP address (LAN, VPN or a forwarded port).
        Transport=steam

        # Direct transport only: the address F6 joins, and the TCP port a host listens on.
        DirectAddress=127.0.0.1
        DirectPort=27961

        # Players in one game, host included (2-8).
        MaxPlayers=4

        # Local port between the game and BatCoopLink.exe. Change only if something else uses it.
        LinkPort=27960

        # 1 = keep the game running when its window is not focused (the game normally pauses, which also
        # freezes you for the other players).
        RunInBackground=0

        # 1 = when the game starts at a tiny saved resolution (a shrunk window saved on quit), put back the one
        # from the game's launcher (or the desktop's) in Documents\WB Games\...\BmEngine.ini.
        DisplayGuard=1

        # Chat and host menu keys (Unreal key names: T, Y, F3, F4, ...). Both can also be changed in the host menu.
        # In a co-op game the chat key is taken by the chat; T is also the game's "secondary group combat move".
        ChatKey=T
        MenuKey=F3

        # The host's game settings (also changed in the host menu, which saves them here).
        # PVP is a draft and switched off in this version; these two keys are ignored for now.
        HostPvp=0
        HostPvpDamage=100
        HostAllowTeleport=1
        HostNameTags=1
        # vote = everyone watching must press skip; host = the host's skip skips for everyone
        HostCutsceneSkip=vote
        HostEventFeed=1

        # animated = other players walk, run and fight with the game's own animations; kinematic = 0.2 behaviour
        PuppetMode=animated
        """;
}
