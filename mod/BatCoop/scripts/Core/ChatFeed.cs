#nullable enable
// The chat: lines typed by players and event lines (a mission completed, a player defeated another, a player was
// taken down by enemies). Event lines are produced on the machine that saw the event and sent to everyone.
using System;
using System.Collections.Generic;

namespace BatCoop.Core;

public enum FeedKind : byte { Chat = 0, Mission = 1, Defeated = 2, DiedToEnemy = 3, Joined = 4, Left = 5, System = 6 }

public readonly record struct FeedLine(FeedKind Kind, string Who, string Text, double At);

public static class FeedMessages
{
    /// <summary>An event line: kind, the player it is about, and the rest of the sentence.</summary>
    public static byte[] Event(FeedKind kind, string who, string text) =>
        new WireWriter().U8((byte)GameMsg.Event).U8((byte)kind).Str(who).Str(text).ToArray();

    public static FeedLine DecodeEvent(ref WireReader r, double now) => new((FeedKind)r.U8(), r.Str(), r.Str(), now);

    /// <summary>How a line reads in the chat box.</summary>
    public static string Format(in FeedLine l) => l.Kind switch
    {
        FeedKind.Chat => $"{l.Who}: {l.Text}",
        FeedKind.Mission => $"* {l.Who} completed {l.Text}",
        FeedKind.Defeated => $"* {l.Who} defeated {l.Text}",
        FeedKind.DiedToEnemy => $"* {l.Who} was taken down by {l.Text}",
        FeedKind.Joined => $"* {l.Who} joined the game",
        FeedKind.Left => $"* {l.Who} left the game",
        _ => $"* {l.Text}",
    };

    /// <summary>"OW_Mission_FindTheCure" -> "Find The Cure": readable text from a game name.</summary>
    public static string Humanize(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "None") return "";
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var keep = new List<string>();
        foreach (var p in parts)
        {
            // Drop map prefixes and short codes ("OW", "Obj", "A1", "Ch2").
            if (p.Length <= 3 && (p.ToUpperInvariant() == p || char.IsDigit(p[^1]))) continue;
            if (p is "Obj" or "Objective" or "Mission" or "Primary" or "Side") continue;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < p.Length; i++)
            {
                if (i > 0 && char.IsUpper(p[i]) && !char.IsUpper(p[i - 1])) sb.Append(' ');
                sb.Append(p[i]);
            }
            keep.Add(sb.ToString());
        }
        return string.Join(' ', keep);
    }
}

/// <summary>The lines on screen. Lines fade after a while unless the chat box is open.</summary>
public sealed class ChatLog
{
    readonly List<FeedLine> _lines = [];
    public int Max = 50;
    public IReadOnlyList<FeedLine> Lines => _lines;
    public event Action<FeedLine>? Added;

    public void Add(in FeedLine l)
    {
        _lines.Add(l);
        if (_lines.Count > Max) _lines.RemoveAt(0);
        Added?.Invoke(l);
    }

    public IEnumerable<FeedLine> Recent(double now, double seconds, int count)
    {
        int start = Math.Max(0, _lines.Count - count);
        for (int i = start; i < _lines.Count; i++)
            if (now - _lines[i].At <= seconds) yield return _lines[i];
    }

    public IEnumerable<FeedLine> Last(int count)
    {
        for (int i = Math.Max(0, _lines.Count - count); i < _lines.Count; i++) yield return _lines[i];
    }
}

/// <summary>Host: turns committed story changes into "mission complete" lines.</summary>
public static class MissionFeed
{
    /// <summary>A line for one commit, or null. Chapters and side-mission chapters only go up when a mission
    /// step is done; a new primary objective means the previous one was completed.</summary>
    public static string? Describe(IReadOnlyList<WorldEffect> diff, WorldState before)
    {
        if (before.Values.Count == 0) return null; // the first commit of a room is the baseline, not progress
        foreach (var e in diff)
        {
            if (e.Kind == WorldValueKind.Str && e.Key == "obj:primary" && before.TryGet(e.Key, out var old) && old.Str.Length > 0 && old.Str != "None" && old.Str != e.Str)
            {
                var what = FeedMessages.Humanize(old.Str);
                return what.Length > 0 ? $"the objective \"{what}\"" : "an objective";
            }
        }
        foreach (var e in diff)
        {
            if (e.Kind != WorldValueKind.Int) continue;
            if (!before.TryGet(e.Key, out var o)) continue;
            int was = o.Int;
            if (e.Key == "ch:main" && e.Int > was) return $"story chapter {was}";
            if (e.Key.StartsWith("ch:side:") && e.Int > was && was > 0) return $"a step of side mission {int.Parse(e.Key[8..]) + 1}";
        }
        return null;
    }
}
