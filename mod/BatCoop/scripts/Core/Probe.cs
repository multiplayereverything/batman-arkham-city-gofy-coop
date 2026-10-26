#nullable enable
// Writes coop-anything animcheck probe streams (JSONL, see toolkits/coop-anything/tools/animcheck/FORMAT.md).
// One file per machine; the lab compares the owner's own player with the puppet other machines show.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace BatCoop.Core;

public readonly record struct ProbeActor(string Ref, Vector3 Pos, float PitchDeg, float YawDeg, bool Player, bool Dead, bool Valid);

public sealed class ProbeWriter : IDisposable
{
    readonly StreamWriter _w;
    readonly string _machine;
    long _frame;

    public ProbeWriter(string path, string machine)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _w = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        _machine = machine;
    }

    public void Write(double nowSeconds, IEnumerable<ProbeActor> actors)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"machine\":\"").Append(_machine).Append("\",\"t_ms\":").Append(F(nowSeconds * 1000.0))
          .Append(",\"rx_ms\":").Append(F(DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalMilliseconds))
          .Append(",\"frame\":").Append(_frame++).Append(",\"actors\":[");
        bool first = true;
        foreach (var a in actors)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"ref\":\"").Append(a.Ref).Append("\",\"pos\":[")
              .Append(F(a.Pos.X)).Append(',').Append(F(a.Pos.Y)).Append(',').Append(F(a.Pos.Z))
              .Append("],\"rot\":[").Append(F(a.PitchDeg * Math.PI / 180)).Append(",0,").Append(F(a.YawDeg * Math.PI / 180))
              .Append("],\"cell\":0,\"player\":").Append(a.Player ? "true" : "false")
              .Append(",\"dead\":").Append(a.Dead ? "true" : "false")
              .Append(",\"valid3d\":").Append(a.Valid ? "true" : "false").Append('}');
        }
        sb.Append("]}");
        _w.WriteLine(sb.ToString());
    }

    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public void Dispose() => _w.Dispose();
}
