#nullable enable
// Puts a usable resolution back when the game started at a tiny one (see Core/DisplaySettings.cs). The game has
// already read its settings by the time mods load and writes them back when it quits, so:
// - now: the window is resized to the repaired size; the game takes a window's new size as its resolution,
//   so this session is playable and quits with a sane (windowed) size;
// - after it quits: BatCoopLink.exe --fix-display waits for the game to close and writes the repaired
//   resolution (fullscreen when that was the choice), so the next start opens at it.
// Starts with -windowed or -ResX/-ResY on the command line (lab copies) chose their size and are left alone.
using System.Diagnostics;
using System.Runtime.InteropServices;
using BatCoop.Core;

namespace BatCoop.Game;

static class DisplayGuard
{
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool AdjustWindowRectEx(ref Rect r, uint style, bool menu, uint exStyle);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref Rect r, uint winIni);
    struct Rect { public int L, T, R, B; }

    /// <summary>The repaired resolution, or null when nothing needed repairing. <paramref name="inWindow"/>: the
    /// game is running in a window now (only then is the window resized; a fullscreen game is left alone).</summary>
    public static Resolution? Check(string linkExe, Action<string> log, out bool inWindow)
    {
        inWindow = false;
        var cmd = Environment.CommandLine;
        if (cmd.Contains("-windowed", StringComparison.OrdinalIgnoreCase) || cmd.Contains("-resx", StringComparison.OrdinalIgnoreCase))
            return null;
        var saved = DisplaySettings.ReadFile(DisplaySettings.EngineIni);
        var fix = DisplaySettings.Repair(saved, DisplaySettings.ReadFile(DisplaySettings.LauncherIni), GetSystemMetrics(0), GetSystemMetrics(1));
        if (fix is not { } r) return null;
        inWindow = saved is { Fullscreen: false };
        log($"display: the game started at {saved?.ToString() ?? "an unreadable resolution"}; restoring {r}");
        try { DisplaySettings.WriteFile(DisplaySettings.EngineIni, r); } catch (Exception e) { log("display: could not write BmEngine.ini: " + e.Message); }
        StartFixAfterExit(linkExe, r, log);
        return r;
    }

    static void StartFixAfterExit(string linkExe, Resolution r, Action<string> log)
    {
        if (!File.Exists(linkExe)) { log("display: BatCoopLink.exe missing; the fix applies only to this session"); return; }
        try
        {
            var psi = new ProcessStartInfo(linkExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(linkExe)! };
            psi.ArgumentList.Add("--fix-display");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(DisplaySettings.EngineIni);
            psi.ArgumentList.Add($"{r.X}x{r.Y}");
            psi.ArgumentList.Add(r.Fullscreen ? "fullscreen" : "windowed");
            Process.Start(psi);
        }
        catch (Exception e) { log("display: could not start the fix-after-exit helper: " + e.Message); }
    }

    /// <summary>Resizes the game's window so its client area is the repaired size (at most the work area).</summary>
    public static bool ResizeWindow(Resolution r, Action<string> log)
    {
        var h = Process.GetCurrentProcess().MainWindowHandle;
        if (h == IntPtr.Zero) return false;
        var work = new Rect();
        SystemParametersInfo(0x30 /* SPI_GETWORKAREA */, 0, ref work, 0);
        var rect = new Rect { R = r.X, B = r.Y };
        uint style = (uint)GetWindowLong(h, -16), ex = (uint)GetWindowLong(h, -20);
        AdjustWindowRectEx(ref rect, style, false, ex);
        int w = rect.R - rect.L, ht = rect.B - rect.T;
        int maxW = work.R - work.L, maxH = work.B - work.T;
        if (maxW > 0 && maxH > 0 && (w > maxW || ht > maxH))
        {
            // Keep the aspect ratio inside the work area.
            double s = Math.Min((double)maxW / w, (double)maxH / ht);
            w = (int)(w * s);
            ht = (int)(ht * s);
        }
        bool ok = SetWindowPos(h, IntPtr.Zero, work.L, work.T, w, ht, 0x0014 /* SWP_NOZORDER | SWP_NOACTIVATE */);
        log($"display: window resized to {w}x{ht} ({(ok ? "ok" : "failed")})");
        return ok;
    }
}
