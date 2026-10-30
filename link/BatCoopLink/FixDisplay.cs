// BatCoopLink.exe --fix-display: the game writes its resolution when it quits, so a repair the mod made
// (DisplayGuard) is written again after the game has gone.
using System.Diagnostics;
using BatCoop.Core;

namespace BatCoop.Link;

static class FixDisplay
{
    public static int Run(int gamePid, string ini, string size, string mode)
    {
        var parts = size.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)) return 2;
        var r = new Resolution(x, y, mode == "fullscreen");
        if (!r.Usable) return 2;
        try { using var game = Process.GetProcessById(gamePid); game.WaitForExit(); }
        catch (ArgumentException) { } // already gone
        // The game's own save happens on the way out; give the file a moment, and retry while it is locked.
        for (int i = 0; i < 20; i++)
        {
            Thread.Sleep(500);
            try { return DisplaySettings.WriteFile(ini, r) ? 0 : 1; }
            catch (IOException) { }
        }
        return 1;
    }
}
