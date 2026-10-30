# Prints the peak audio level of one process's Windows audio session, sampled for a few seconds.
#   powershell -File lab/AudioMeter.ps1 -ProcessId <pid> [-Seconds 3]
# Output: one line "peak_max=<0..1> peak_avg=<0..1> samples=<n>" (no session: "no audio session").
param([int]$ProcessId, [double]$Seconds = 3)
Add-Type -Language CSharp @"
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] public class MMDeviceEnumerator {}
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator { int NotImpl1(); int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev); }
[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice { int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionManager2 { int NotImpl1(); int NotImpl2(); int GetSessionEnumerator(out IAudioSessionEnumerator e); }
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEnumerator { int GetCount(out int n); int GetSession(int i, out IAudioSessionControl2 s); }
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl2 {
  int a(); int b(); int c(); int d(); int e(); int f(); int g(); int h(); int i();
  int GetSessionIdentifier(out IntPtr s); int GetSessionInstanceIdentifier(out IntPtr s); int GetProcessId(out uint pid); }
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioMeterInformation { int GetPeakValue(out float peak); }
public static class Meter {
  public static IAudioMeterInformation Find(uint pid) {
    var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
    IMMDevice dev; en.GetDefaultAudioEndpoint(0, 1, out dev);
    var iid = typeof(IAudioSessionManager2).GUID; object o; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
    IAudioSessionEnumerator se; ((IAudioSessionManager2)o).GetSessionEnumerator(out se);
    int n; se.GetCount(out n);
    for (int i = 0; i < n; i++) {
      IAudioSessionControl2 s; se.GetSession(i, out s); uint p; s.GetProcessId(out p);
      if (p == pid) return (IAudioMeterInformation)s;
    }
    return null;
  }
  public static string Sample(uint pid, int ms) {
    var m = Find(pid);
    if (m == null) return "no audio session";
    float max = 0, sum = 0; int n = 0;
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end) { float v; m.GetPeakValue(out v); max = Math.Max(max, v); sum += v; n++; System.Threading.Thread.Sleep(50); }
    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "peak_max={0:0.0000} peak_avg={1:0.0000} samples={2}", max, n > 0 ? sum / n : 0, n);
  }
}
"@
[Meter]::Sample([uint32]$ProcessId, [int]($Seconds * 1000))
