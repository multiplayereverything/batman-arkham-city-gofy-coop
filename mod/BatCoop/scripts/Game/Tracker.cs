#nullable enable
// Finding the other players in the open city:
// - on screen: a marker over each remote player in the same world with name, distance and height, drawn through
//   walls; when the player is off screen or behind the camera the marker sits on the screen edge pointing the way;
// - a radar on the right of the screen: a top-down view around you (the camera's direction is up) with every
//   remote player as a dot and their name, so a friend across the city is easy to head towards.
// The game's own map screen cannot show them: it draws only icons from its mission and waypoint data, and an
// objective-marker actor of ours never appeared there (lab, MODLOG 0.4.0). F4 cycles: markers and radar ->
// markers only -> off.
using System.Numerics;
using BatCoop.Core;
using BmSDK;
using BmSDK.BmGame;
using BmSDK.Engine;

namespace BatCoop.Game;

public enum TrackMode { All, ScreenOnly, Off }

sealed class Tracker
{
    public TrackMode Mode = TrackMode.All;
    /// <summary>Lab: what the last frame drew (player id -> on screen / edge / radar position).</summary>
    public readonly Dictionary<byte, string> LastDrawn = [];

    /// <summary>Where a remote player is in our world, or null (not in a world, another map, no data yet).</summary>
    public static Vector3? WhereIs(RemotePlayer rp, PuppetManager? puppets, string localWorld)
    {
        if (!rp.Buffer.HasData || !rp.Buffer.Newest.InWorld) return null;
        if (rp.Info != null && rp.Info.World.Length > 0 && localWorld.Length > 0
            && !rp.Info.World.Equals(localWorld, StringComparison.OrdinalIgnoreCase)) return null;
        if (puppets != null && puppets.All.TryGetValue(rp.Id, out var p) && p.Pawn.IsValid) return p.Pawn.Location;
        return rp.Buffer.Newest.Pos;
    }

    static string Label(RemotePlayer rp, Vector3 pos, Vector3 me)
    {
        float dist = Vector3.Distance(pos, me) / 100f;
        float dz = (pos.Z - me.Z) / 100f;
        return $"{rp.Name}  {dist:0} m{(Math.Abs(dz) >= 5 ? (dz > 0 ? $"  {dz:0} m up" : $"  {-dz:0} m down") : "")}";
    }

    /// <summary>Screen markers and the radar, from the HUD.</summary>
    public void Draw(HUD hud, Canvas c, CoopSession s, PuppetManager? puppets, string localWorld, Vector3 me)
    {
        LastDrawn.Clear();
        if (Mode == TrackMode.Off || hud.PlayerOwner == null) return;
        hud.PlayerOwner.GetPlayerViewPoint(out var camLoc, out var camRot);
        var fwd = camRot.ToDirection();
        var right = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, 1), fwd));   // UE: X forward, Y right, Z up
        var up = Vector3.Cross(fwd, right);
        var players = new List<(RemotePlayer rp, Vector3 pos)>();
        foreach (var rp in s.Remotes.Values)
            if (WhereIs(rp, puppets, localWorld) is { } pos) players.Add((rp, pos));
        if (players.Count == 0) return;

        float w = c.ClipX, h = c.ClipY, margin = 40;
        foreach (var (rp, pos) in players)
        {
            var head = pos + new Vector3(0, 0, 150);
            var rel = head - camLoc;
            float dist = Vector3.Distance(pos, me) / 100f;
            string label = Label(rp, pos, me);
            float sx = 0, sy = 0;
            bool onScreen = false;
            if (Vector3.Dot(rel, fwd) > 0)
            {
                var sp = c.Project(head);
                sx = sp.X; sy = sp.Y;
                onScreen = sx >= margin && sx <= w - margin && sy >= margin && sy <= h - margin;
            }
            string arrow = "";
            if (onScreen)
            {
                if (dist < 12) { LastDrawn[rp.Id] = "close"; continue; } // in view and near: the name tag is enough
            }
            else
            {
                // Off screen: on the edge in the direction of the player.
                float x = Vector3.Dot(rel, right), y = -Vector3.Dot(rel, up);
                if (Vector3.Dot(rel, fwd) <= 0 && Math.Abs(x) < 1) x = 1; // straight behind: pick a side
                var d = Vector2.Normalize(new Vector2(x, y));
                float scale = Math.Min((w / 2 - margin) / Math.Max(Math.Abs(d.X), 0.001f), (h / 2 - margin) / Math.Max(Math.Abs(d.Y), 0.001f));
                sx = w / 2 + d.X * scale;
                sy = h / 2 + d.Y * scale;
                arrow = Math.Abs(d.X) > Math.Abs(d.Y) ? (d.X > 0 ? ">" : "<") : (d.Y > 0 ? "v" : "^");
            }
            string text = arrow == "<" ? "< " + label : arrow.Length > 0 ? label + " " + arrow : label;
            c.TextSize(text, out var xl, out var yl);
            float tx = Math.Clamp(sx - xl / 2, 4, w - xl - 4);
            float ty = Math.Clamp(sy - yl, 4, h - yl - 4);
            Box(c, tx - 4, ty - 2, xl + 8, yl + 4);
            Text(c, tx, ty, text);
            LastDrawn[rp.Id] = onScreen ? "marker" : "edge " + arrow;
        }

        if (Mode == TrackMode.All) DrawRadar(c, players, me, camRot.Yaw);
    }

    /// <summary>Top-down, camera direction up, sized to the farthest player (50 m to 1 km); players beyond the
    /// edge sit on it.</summary>
    void DrawRadar(Canvas c, List<(RemotePlayer rp, Vector3 pos)> players, Vector3 me, float camYawUnits)
    {
        float size = Math.Clamp(c.ClipY * 0.26f, 110, 220);
        float x0 = c.ClipX - size - 16, y0 = c.ClipY * 0.2f;
        float cx = x0 + size / 2, cy = y0 + size / 2, r = size / 2 - 8;
        float far = 0;
        foreach (var (_, pos) in players) far = Math.Max(far, Vector2.Distance(new(pos.X, pos.Y), new(me.X, me.Y)));
        float range = Math.Clamp(far * 1.15f, 5000, 100000);   // uu
        float yaw = camYawUnits * MathF.PI * 2 / 65536f;
        var fwd2 = new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
        var right2 = new Vector2(-fwd2.Y, fwd2.X);

        Box(c, x0, y0, size, size + 18);
        c.SetDrawColor(120, 230, 255, 70);
        c.SetPos(cx - r, cy, 0); c.DrawRect(2 * r, 1, c.DefaultTexture);
        c.SetPos(cx, cy - r, 0); c.DrawRect(1, 2 * r, c.DefaultTexture);
        c.SetDrawColor(255, 255, 255, 255);
        c.SetPos(cx - 3, cy - 3, 0); c.DrawRect(6, 6, c.DefaultTexture);   // you
        Text(c, x0 + 6, y0 + size, $"radar  {range / 100:0} m", 170, 170, 170);

        foreach (var (rp, pos) in players)
        {
            var rel = new Vector2(pos.X - me.X, pos.Y - me.Y);
            float ahead = Vector2.Dot(rel, fwd2), side = Vector2.Dot(rel, right2);
            var p = new Vector2(side, -ahead) / range * r;
            if (p.Length() > r) p = Vector2.Normalize(p) * r;
            float px = cx + p.X, py = cy + p.Y;
            c.SetDrawColor(120, 230, 255, 255);
            c.SetPos(px - 4, py - 4, 0); c.DrawRect(8, 8, c.DefaultTexture);
            c.TextSize(rp.Name, out var xl, out var yl);
            Text(c, Math.Clamp(px - xl / 2, x0 + 2, x0 + size - xl - 2), Math.Clamp(py + 6, y0 + 2, y0 + size - yl), rp.Name);
            LastDrawn[(byte)(rp.Id | 0x80)] = $"radar {px - cx:0},{py - cy:0}";
        }
    }

    static void Box(Canvas c, float x, float y, float w, float h)
    {
        c.SetDrawColor(0, 0, 0, 140);
        c.SetPos(x, y, 0);
        c.DrawRect(w, h, c.DefaultTexture);
    }

    static void Text(Canvas c, float x, float y, string s, byte r = 120, byte g = 230, byte b = 255)
    {
        c.SetPos(x, y, 0);
        c.SetDrawColor(r, g, b, 255);
        c.DrawText(s, false, 1, 1, out _);
    }
}
