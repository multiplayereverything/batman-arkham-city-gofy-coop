#nullable enable
// Test bridge for the lab (coop-anything tools/bridge pattern): a line protocol on 127.0.0.1 that queues
// commands for the game thread and answers with one JSON line each. Off unless LabPort is set.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BatCoop.Game;

public sealed class LabBridge : IDisposable
{
    public sealed record Request(string Line, Action<string> Reply);

    readonly TcpListener _listener;
    readonly ConcurrentQueue<Request> _queue = new();
    volatile bool _closed;

    public int Port { get; }

    public LabBridge(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(AcceptLoop) { IsBackground = true, Name = "BatCoop lab bridge" }.Start();
    }

    void AcceptLoop()
    {
        while (!_closed)
        {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); } catch { return; }
            new Thread(() => Serve(c)) { IsBackground = true, Name = "BatCoop lab client" }.Start();
        }
    }

    void Serve(TcpClient c)
    {
        using (c)
        {
            var stream = c.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            var writeLock = new object();
            try
            {
                string? line;
                while (!_closed && (line = reader.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    var done = new ManualResetEventSlim();
                    string answer = "";
                    _queue.Enqueue(new Request(line.Trim(), a => { answer = a; done.Set(); }));
                    if (!done.Wait(30000)) answer = "{\"ok\":false,\"error\":\"game thread did not answer in 30 s\"}";
                    lock (writeLock) writer.WriteLine(answer);
                }
            }
            catch { }
        }
    }

    public bool TryTake(out Request req) => _queue.TryDequeue(out req!);

    public void Dispose()
    {
        _closed = true;
        try { _listener.Stop(); } catch { }
        while (_queue.TryDequeue(out var r)) r.Reply("{\"ok\":false,\"error\":\"bridge closed\"}");
    }

    public static string Json(params (string key, object? value)[] fields)
    {
        var sb = new StringBuilder("{");
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(fields[i].key).Append("\":").Append(Value(fields[i].value));
        }
        return sb.Append('}').ToString();
    }

    public static string Value(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"",
        float f => float.IsFinite(f) ? f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "null",
        double d => double.IsFinite(d) ? d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "null",
        System.Numerics.Vector3 v3 => $"[{Value(v3.X)},{Value(v3.Y)},{Value(v3.Z)}]",
        RawJson r => r.Text,
        System.Collections.IEnumerable e => "[" + string.Join(",", e.Cast<object?>().Select(Value)) + "]",
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
    };

    public sealed record RawJson(string Text);
}
