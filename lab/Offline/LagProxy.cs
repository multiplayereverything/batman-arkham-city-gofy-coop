using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace BatCoop.Lab;

/// <summary>TCP proxy that delays every chunk by base + jitter milliseconds in both directions. Delivery stays in
/// order (as TCP would), so jitter shows up as bunching, which is what a receiver has to smooth.</summary>
public sealed class LagProxy : IDisposable
{
    readonly TcpListener _listener;
    readonly int _target;
    readonly int _baseMs, _jitterMs;
    readonly Random _rng = new(1234);
    volatile bool _closed;

    public int Port { get; }

    public LagProxy(int targetPort, int baseMs, int jitterMs)
    {
        _target = targetPort;
        _baseMs = baseMs;
        _jitterMs = jitterMs;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(Accept) { IsBackground = true }.Start();
    }

    void Accept()
    {
        while (!_closed)
        {
            TcpClient a;
            try { a = _listener.AcceptTcpClient(); } catch { return; }
            var b = new TcpClient { NoDelay = true };
            b.Connect(IPAddress.Loopback, _target);
            a.NoDelay = true;
            Pump(a.GetStream(), b.GetStream(), a, b);
            Pump(b.GetStream(), a.GetStream(), b, a);
        }
    }

    void Pump(NetworkStream from, NetworkStream to, TcpClient fa, TcpClient ta)
    {
        var q = new BlockingCollection<(DateTime due, byte[] data)>();
        new Thread(() =>
        {
            var buf = new byte[65536];
            DateTime lastDue = DateTime.MinValue;
            try
            {
                int n;
                while ((n = from.Read(buf, 0, buf.Length)) > 0)
                {
                    int delay;
                    lock (_rng) delay = _baseMs + _rng.Next(-_jitterMs, _jitterMs + 1);
                    var due = DateTime.UtcNow.AddMilliseconds(Math.Max(0, delay));
                    if (due < lastDue) due = lastDue;
                    lastDue = due;
                    q.Add((due, buf.AsSpan(0, n).ToArray()));
                }
            }
            catch { }
            q.CompleteAdding();
        }) { IsBackground = true }.Start();
        new Thread(() =>
        {
            try
            {
                foreach (var (due, data) in q.GetConsumingEnumerable())
                {
                    var wait = due - DateTime.UtcNow;
                    if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                    to.Write(data);
                }
            }
            catch { }
            try { ta.Close(); fa.Close(); } catch { }
        }) { IsBackground = true }.Start();
    }

    public void Dispose()
    {
        _closed = true;
        _listener.Stop();
    }
}
