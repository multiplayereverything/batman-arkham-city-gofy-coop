#nullable enable
// Small Win32 helpers: the clipboard (lobby ids) without WinForms.
using System.Runtime.InteropServices;

namespace BatCoop.Game;

static partial class Native
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);

    const uint CF_UNICODETEXT = 13;
    const uint GMEM_MOVEABLE = 2;

    public static bool SetClipboard(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            var p = GlobalLock(mem);
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(mem);
            return SetClipboardData(CF_UNICODETEXT, mem) != IntPtr.Zero;
        }
        finally { CloseClipboard(); }
    }

    public static string GetClipboard()
    {
        if (!OpenClipboard(IntPtr.Zero)) return "";
        try
        {
            var mem = GetClipboardData(CF_UNICODETEXT);
            if (mem == IntPtr.Zero) return "";
            var p = GlobalLock(mem);
            try { return Marshal.PtrToStringUni(p) ?? ""; }
            finally { GlobalUnlock(mem); }
        }
        finally { CloseClipboard(); }
    }
}
