#nullable enable
// Lab only: lets a second game copy start on the same PC. The game refuses a second copy while a named
// single-instance object exists; this lists the named mutexes, events and semaphores of OUR OWN process and
// closes our own handle to the one the lab names. Nothing in another process is touched (coop-anything
// playbook: multi-instance-testing, the Gothic 3 fix). Reached only through the lab bridge (LabPort).
using System.Runtime.InteropServices;

namespace BatCoop.Game;

static class LabInstances
{
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int needed);
    [DllImport("ntdll.dll")] static extern int NtQueryObject(IntPtr h, int cls, IntPtr buf, int len, out int needed);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    const int SystemExtendedHandleInformation = 64;
    const int ObjectNameInformation = 1, ObjectTypeInformation = 2;

    public sealed record Named(long Handle, string Type, string Name);

    /// <summary>Named Mutant/Event/Semaphore/Section handles held by this process.</summary>
    public static List<Named> List()
    {
        int pid = Environment.ProcessId;
        int len = 1 << 22;
        IntPtr buf = IntPtr.Zero;
        try
        {
            for (; ; )
            {
                buf = Marshal.AllocHGlobal(len);
                int st = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, len, out int needed);
                if (st == unchecked((int)0xC0000004)) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; len = Math.Max(len * 2, needed + 65536); continue; }
                if (st != 0) throw new InvalidOperationException($"NtQuerySystemInformation 0x{st:X8}");
                break;
            }
            int ps = IntPtr.Size;
            long count = ps == 8 ? Marshal.ReadInt64(buf) : Marshal.ReadInt32(buf);
            // SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX: Object, UniqueProcessId, HandleValue (pointer sized), then
            // GrantedAccess (u32), CreatorBackTraceIndex (u16), ObjectTypeIndex (u16), HandleAttributes (u32), Reserved (u32).
            int entry = ps * 3 + 16;
            var result = new List<Named>();
            for (long i = 0; i < count; i++)
            {
                var e = buf + ps * 2 + (int)(i * entry);
                long owner = ps == 8 ? Marshal.ReadInt64(e + ps) : Marshal.ReadInt32(e + ps);
                if (owner != pid) continue;
                var h = Marshal.ReadIntPtr(e + ps * 2);
                var type = Query(h, ObjectTypeInformation);
                if (type is not ("Mutant" or "Event" or "Semaphore" or "Section")) continue;
                var name = Query(h, ObjectNameInformation);
                if (name.Length > 0) result.Add(new Named(h.ToInt64(), type, name));
            }
            return result;
        }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
    }

    /// <summary>The UNICODE_STRING at the start of a name or type information block.</summary>
    static string Query(IntPtr h, int cls)
    {
        int len = 2048;
        var b = Marshal.AllocHGlobal(len);
        try
        {
            if (NtQueryObject(h, cls, b, len, out _) != 0) return "";
            int chars = Marshal.ReadInt16(b) / 2;
            var p = Marshal.ReadIntPtr(b + IntPtr.Size);
            return chars > 0 && p != IntPtr.Zero ? Marshal.PtrToStringUni(p, chars) : "";
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    /// <summary>Closes this process's own handles whose name ends with <paramref name="nameEnd"/>.</summary>
    public static int Release(string nameEnd)
    {
        int closed = 0;
        foreach (var n in List())
            if (n.Name.EndsWith(nameEnd, StringComparison.OrdinalIgnoreCase) && CloseHandle((IntPtr)n.Handle)) closed++;
        return closed;
    }
}
