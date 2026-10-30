#nullable enable
// The chat box and the host menu, drawn on the HUD canvas. Keys reach them through the console's InputKey and
// InputChar (InputHooks): the console sees every key before the player controller does, so while the chat box or
// the menu is open no key reaches the game (typing "w" does not walk), and the chat key itself is taken in a co-op
// game (T is also the game's "secondary group combat move" key; outside a co-op game T works as before).
using BatCoop.Core;
using BmSDK;
using BmSDK.Engine;

namespace BatCoop.Game;

public sealed class CoopUi
{
    readonly Func<CoopSession?> _session;
    readonly Func<double> _now;
    readonly Action<string, string> _saveIni;       // key, value -> BatCoop.ini
    readonly Action<HostSettings> _applySettings;    // host only

    public string ChatKey;
    public string MenuKey;
    public bool ChatOpen { get; private set; }
    public bool MenuOpen { get; private set; }
    string _typed = "";
    int _menuRow;
    int _rebinding = -1;   // menu row waiting for a key, or -1
    double _blinkStart;
    public long LinesSent;

    public CoopUi(Func<CoopSession?> session, Func<double> now, Action<string, string> saveIni, Action<HostSettings> applySettings,
                  string chatKey, string menuKey)
    {
        _session = session;
        _now = now;
        _saveIni = saveIni;
        _applySettings = applySettings;
        ChatKey = chatKey;
        MenuKey = menuKey;
    }

    public bool Capturing => ChatOpen || MenuOpen;

    // ---------- input ----------

    /// <summary>A key event from the console. Returns true when the key is ours (the game never sees it).</summary>
    public bool OnKey(string key, bool pressed)
    {
        var s = _session();
        if (MenuOpen)
        {
            if (pressed) MenuKeyPressed(key);
            return true;
        }
        if (ChatOpen)
        {
            if (!pressed) return true;
            switch (key)
            {
                case "Enter":
                    if (_typed.Trim().Length > 0 && s is { InRoom: true }) { s.SendChat(_typed); LinesSent++; }
                    CloseChat();
                    break;
                case "Escape": CloseChat(); break;
                case "BackSpace": if (_typed.Length > 0) _typed = _typed[..^1]; break;
            }
            return true;
        }
        if (s is not { InRoom: true }) return false;
        if (pressed && Same(key, ChatKey)) { OpenChat(); return true; }
        if (pressed && Same(key, MenuKey)) { MenuOpen = true; _menuRow = 0; _rebinding = -1; return true; }
        // The release of the key that opened the box also belongs to us.
        return Same(key, ChatKey) || Same(key, MenuKey);
    }

    /// <summary>A typed character from the console (only while the chat box is open).</summary>
    public bool OnChar(string text)
    {
        if (!ChatOpen) return MenuOpen;
        foreach (var c in text)
        {
            if (char.IsControl(c)) continue;
            if (_typed.Length < 120) _typed += c;
        }
        return true;
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void OpenChat()
    {
        ChatOpen = true;
        _typed = "";
        _blinkStart = _now();
        // The key that opened the box arrives as a character right after; drop it.
        _dropNextChar = true;
    }
    bool _dropNextChar;

    public bool TakeOpeningChar(string text)
    {
        if (!_dropNextChar) return false;
        _dropNextChar = false;
        return text.Length == 1 && Same(text, ChatKey);
    }

    public void CloseChat() { ChatOpen = false; _typed = ""; _dropNextChar = false; }
    public void CloseAll() { CloseChat(); MenuOpen = false; _rebinding = -1; }

    // ---------- the host menu ----------

    sealed record Row(string Label, Func<HostSettings, string> Value, Func<HostSettings, int, HostSettings>? Change, bool HostOnly);

    static readonly int[] DamageSteps = [50, 75, 100, 150, 200];

    static readonly Row[] Rows =
    [
        new("PVP (fight other players)", s => s.Pvp ? "ON" : "OFF", (s, d) => s with { Pvp = !s.Pvp }, true),
        new("PVP damage", s => $"{s.PvpDamagePercent}%", (s, d) => s with { PvpDamagePercent = Step(DamageSteps, s.PvpDamagePercent, d) }, true),
        new("Go to friend (F8)", s => s.AllowTeleport ? "ALLOWED" : "OFF", (s, d) => s with { AllowTeleport = !s.AllowTeleport }, true),
        new("Name tags", s => s.NameTags ? "ON" : "OFF", (s, d) => s with { NameTags = !s.NameTags }, true),
        new("Cutscene skip", s => s.CutsceneSkip == SkipMode.Vote ? "VOTE" : "HOST ONLY", (s, d) => s with { CutsceneSkip = s.CutsceneSkip == SkipMode.Vote ? SkipMode.HostOnly : SkipMode.Vote }, true),
        new("Event messages in chat", s => s.EventFeed ? "ON" : "OFF", (s, d) => s with { EventFeed = !s.EventFeed }, true),
        new("Chat key", _ => "", null, false),
        new("Host menu key", _ => "", null, false),
        new("Close", _ => "", null, false),
    ];
    const int RowChatKey = 6, RowMenuKey = 7, RowClose = 8;

    static int Step(int[] steps, int cur, int dir)
    {
        int i = Array.IndexOf(steps, cur);
        if (i < 0) i = Array.IndexOf(steps, 100);
        return steps[Math.Clamp(i + (dir < 0 ? -1 : 1), 0, steps.Length - 1)];
    }

    void MenuKeyPressed(string key)
    {
        var s = _session();
        if (_rebinding >= 0)
        {
            if (key != "Escape" && !Reserved(key))
            {
                if (_rebinding == RowChatKey) { ChatKey = key; _saveIni("ChatKey", key); }
                else { MenuKey = key; _saveIni("MenuKey", key); }
            }
            _rebinding = -1;
            return;
        }
        switch (key)
        {
            case "Up": case "W": _menuRow = (_menuRow + Rows.Length - 1) % Rows.Length; break;
            case "Down": case "S": _menuRow = (_menuRow + 1) % Rows.Length; break;
            case "Left": case "A": Change(s, -1); break;
            case "Right": case "D": Change(s, 1); break;
            case "Enter": case "SpaceBar":
                if (_menuRow == RowClose) MenuOpen = false;
                else if (_menuRow is RowChatKey or RowMenuKey) _rebinding = _menuRow;
                else Change(s, 1);
                break;
            case "Escape": MenuOpen = false; break;
            default: if (Same(key, MenuKey)) MenuOpen = false; break;
        }
    }

    /// <summary>Keys BatCoop and the menu itself use; they cannot be the chat or menu key.</summary>
    static bool Reserved(string k) => k is "F5" or "F6" or "F7" or "F8" or "F9" or "F10" or "Enter" or "Up" or "Down" or "Left" or "Right"
        or "BackSpace" or "Tab" or "LeftMouseButton" or "RightMouseButton" or "W" or "A" or "S" or "D" or "SpaceBar";

    void Change(CoopSession? s, int dir)
    {
        if (s == null) return;
        var row = Rows[_menuRow];
        if (row.Change == null) return;
        if (row.HostOnly && !s.IsHost) return;
        _applySettings(row.Change(s.Settings, dir));
    }

    // ---------- drawing ----------

    public void Draw(Canvas c, CoopSession s)
    {
        DrawChat(c, s);
        if (MenuOpen) DrawMenu(c, s);
    }

    static void Box(Canvas c, float x, float y, float w, float h, byte a)
    {
        c.SetPos(x, y, 0);
        c.SetDrawColor(0, 0, 0, a);
        c.DrawRect(w, h, c.DefaultTexture);
    }

    static void Text(Canvas c, float x, float y, string s, byte r, byte g, byte b, byte a = 255)
    {
        c.SetPos(x, y, 0);
        c.SetDrawColor(r, g, b, a);
        c.DrawText(s, false, 1, 1, out _);
    }

    void DrawChat(Canvas c, CoopSession s)
    {
        double now = _now();
        float line = 18, w = Math.Min(620, c.ClipX * 0.55f);
        float x = 24, bottom = c.ClipY * 0.72f;
        var lines = ChatOpen ? s.Feed.Last(10).ToList() : s.Feed.Recent(now, 12, 6).ToList();
        int n = lines.Count + (ChatOpen ? 1 : 0);
        if (n == 0) return;
        float top = bottom - n * line - 8;
        Box(c, x - 6, top - 4, w, n * line + 12, ChatOpen ? (byte)150 : (byte)90);
        float y = top;
        foreach (var l in lines)
        {
            var (r, g, b) = l.Kind switch
            {
                FeedKind.Chat => ((byte)255, (byte)255, (byte)255),
                FeedKind.Mission => ((byte)255, (byte)220, (byte)90),
                FeedKind.Defeated => ((byte)255, (byte)120, (byte)90),
                FeedKind.DiedToEnemy => ((byte)255, (byte)150, (byte)150),
                _ => ((byte)170, (byte)200, (byte)255),
            };
            // Fade the last two seconds of a line when the box is closed.
            double age = now - l.At;
            byte alpha = ChatOpen || age < 10 ? (byte)255 : (byte)Math.Clamp(255 * (12 - age) / 2, 0, 255);
            Text(c, x, y, Fit(c, FeedMessages.Format(l), w - 12), r, g, b, alpha);
            y += line;
        }
        if (ChatOpen)
        {
            bool caret = ((now - _blinkStart) % 1.0) < 0.6;
            Text(c, x, y + 2, Fit(c, "Say: " + _typed, w - 24) + (caret ? "_" : ""), 150, 210, 255);
        }
    }

    static string Fit(Canvas c, string s, float width)
    {
        c.TextSize(s, out var xl, out _);
        if (xl <= width) return s;
        while (s.Length > 4)
        {
            s = s[..^2];
            c.TextSize(s + "...", out xl, out _);
            if (xl <= width) return s + "...";
        }
        return s;
    }

    void DrawMenu(Canvas c, CoopSession s)
    {
        float w = 460, line = 22, h = (Rows.Length + 4) * line;
        float x = (c.ClipX - w) / 2, y = (c.ClipY - h) / 2;
        Box(c, x, y, w, h, 200);
        bool host = s.IsHost;
        Text(c, x + 16, y + 10, host ? "BatCoop - Host settings" : "BatCoop - Game settings (set by the host)", 255, 220, 90);
        float ry = y + 10 + line * 1.5f;
        for (int i = 0; i < Rows.Length; i++)
        {
            var row = Rows[i];
            bool sel = i == _menuRow;
            string value = i switch
            {
                RowChatKey => _rebinding == RowChatKey ? "press a key..." : ChatKey,
                RowMenuKey => _rebinding == RowMenuKey ? "press a key..." : MenuKey,
                _ => row.Value(s.Settings),
            };
            bool locked = row.HostOnly && !host;
            byte gray = locked ? (byte)150 : (byte)255;
            if (sel) { c.SetPos(x + 8, ry - 1, 0); c.SetDrawColor(60, 90, 140, 200); c.DrawRect(w - 16, line, c.DefaultTexture); }
            Text(c, x + 16, ry, (sel ? "> " : "  ") + row.Label, gray, gray, gray);
            if (value.Length > 0)
            {
                c.TextSize(value, out var vl, out _);
                Text(c, x + w - 24 - vl, ry, value, (byte)(locked ? 150 : 150), (byte)(locked ? 150 : 210), 255);
            }
            ry += line;
        }
        Text(c, x + 16, ry + line * 0.5f, host ? "Up/Down choose   Left/Right/Enter change   Esc close" : "Only the host can change these. Esc close", 170, 170, 170);
    }
}
