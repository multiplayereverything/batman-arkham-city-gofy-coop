#nullable enable
// The game's resolution lives in BmEngine.ini [SystemSettings] (Fullscreen, ResX, ResY). The game reads it before
// any mod loads and writes it back when it quits; quitting while it runs in a window saves the window's size,
// so a shrunk window (a few hundred pixels or less) leaves a tiny resolution that every later start opens at.
// These helpers read, choose and write a usable resolution; the mod and BatCoopLink.exe share them.
using System;
using System.IO;

namespace BatCoop.Core;

public readonly record struct Resolution(int X, int Y, bool Fullscreen)
{
    public const int MinX = 640, MinY = 360;
    public bool Usable => X >= MinX && Y >= MinY;
    public override string ToString() => $"{X}x{Y} {(Fullscreen ? "fullscreen" : "windowed")}";
}

public static class DisplaySettings
{
    const string Section = "[SystemSettings]";

    public static string ConfigDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "WB Games", "Batman Arkham City GOTY", "BmGame", "Config");
    /// <summary>What the game uses and saves.</summary>
    public static string EngineIni => Path.Combine(ConfigDir, "BmEngine.ini");
    /// <summary>What the game's launcher (BmLauncher) saves: the player's own choice.</summary>
    public static string LauncherIni => Path.Combine(ConfigDir, "UserEngine.ini");

    /// <summary>Fullscreen/ResX/ResY from the [SystemSettings] section, or null when they are not all there.</summary>
    public static Resolution? Read(string ini)
    {
        int x = -1, y = -1;
        bool? full = null;
        foreach (var (key, value, _, _) in SectionLines(ini))
        {
            if (key.Equals("ResX", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out x);
            else if (key.Equals("ResY", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out y);
            else if (key.Equals("Fullscreen", StringComparison.OrdinalIgnoreCase)) full = value.Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        return x > 0 && y > 0 && full != null ? new Resolution(x, y, full.Value) : null;
    }

    /// <summary>The same text with Fullscreen/ResX/ResY in [SystemSettings] replaced; everything else (other
    /// sections, line endings) stays as it is.</summary>
    public static string Write(string ini, Resolution r)
    {
        var sb = new System.Text.StringBuilder(ini);
        // From the end, so earlier offsets stay valid.
        var lines = new System.Collections.Generic.List<(string key, int start, int length)>();
        foreach (var (key, _, start, length) in SectionLines(ini)) lines.Add((key, start, length));
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var (key, start, length) = lines[i];
            string? v = key.ToLowerInvariant() switch
            {
                "resx" => r.X.ToString(),
                "resy" => r.Y.ToString(),
                "fullscreen" => r.Fullscreen ? "True" : "False",
                _ => null,
            };
            if (v != null) sb.Remove(start, length).Insert(start, key + "=" + v);
        }
        return sb.ToString();
    }

    /// <summary>The resolution to put back when the saved one is unusable, or null when it is fine. The
    /// launcher's choice comes first, then the desktop's size in fullscreen.</summary>
    public static Resolution? Repair(Resolution? saved, Resolution? launcher, int desktopX, int desktopY)
    {
        if (saved is { Usable: true }) return null;
        if (launcher is { Usable: true } l) return l;
        return desktopX >= Resolution.MinX && desktopY >= Resolution.MinY ? new Resolution(desktopX, desktopY, true) : new Resolution(1280, 720, true);
    }

    /// <summary>Rewrites the file's resolution; false when the file or the section is missing.</summary>
    public static bool WriteFile(string path, Resolution r)
    {
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path);
        if (Read(text) == null) return false;
        File.WriteAllText(path, Write(text, r));
        return true;
    }

    public static Resolution? ReadFile(string path)
    {
        try { return File.Exists(path) ? Read(File.ReadAllText(path)) : null; }
        catch (IOException) { return null; }
    }

    /// <summary>key, value, and the line's offset and length (without its line break) for each key=value line
    /// of [SystemSettings].</summary>
    static System.Collections.Generic.IEnumerable<(string key, string value, int start, int length)> SectionLines(string ini)
    {
        bool inside = false;
        int pos = 0;
        while (pos < ini.Length)
        {
            int end = ini.IndexOf('\n', pos);
            int next = end < 0 ? ini.Length : end + 1;
            int len = (end < 0 ? ini.Length : end) - pos;
            if (len > 0 && ini[pos + len - 1] == '\r') len--;
            var line = ini.Substring(pos, len);
            var t = line.Trim();
            if (t.StartsWith('[')) inside = t.Equals(Section, StringComparison.OrdinalIgnoreCase);
            else if (inside)
            {
                int eq = line.IndexOf('=');
                if (eq > 0) yield return (line[..eq].Trim(), line[(eq + 1)..].Trim(), pos, len);
            }
            pos = next;
        }
    }
}
