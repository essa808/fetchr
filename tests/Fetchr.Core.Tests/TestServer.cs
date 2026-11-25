using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Fetchr.Core.Tests;

/// <summary>
/// A tiny HTTP/1.1 file server on 127.0.0.1 with switches for the ways real servers misbehave.
/// One request per connection (Connection: close), so every behaviour is deterministic.
/// </summary>
public sealed class TestServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public byte[] Body { get; set; }
    public bool SupportRanges { get; set; } = true;
    /// <summary>Answer ranged requests with 200 and the whole file, like some CDNs do.</summary>
    public bool IgnoreRangeWith200 { get; set; }
    /// <summary>Cut the connection after this many body bytes, this many times.</summary>
    public int DropAfterBytes { get; set; }
    public int DropsLeft { get => Volatile.Read(ref _dropsLeft); set => Volatile.Write(ref _dropsLeft, value); }
    private int _dropsLeft;
    /// <summary>Send no Content-Length (size unknown until the end).</summary>
    public bool Chunkless { get; set; }
    /// <summary>Delay per 64 KB written, to make a download slow enough to pause.</summary>
    public int ThrottleMs { get; set; }
    /// <summary>Answers whose range starts at or after this offset get <see cref="SlowThrottleMs"/> instead.</summary>
    public long SlowFrom { get; set; } = -1;
    public int SlowThrottleMs { get; set; }
    public string? ContentDisposition { get; set; }
    public string Etag { get; set; } = "\"v1\"";

    public ConcurrentQueue<string?> Ranges { get; } = new();
    public ConcurrentQueue<string?> Cookies { get; } = new();
    /// <summary>Distinct files being served at the same moment (by path), to check the queue limit.</summary>
    public ConcurrentDictionary<string, int> ActivePaths { get; } = new();
    public int MaxActiveFiles;

    public TestServer(byte[] body)
    {
        Body = body;
        _listener.Start();
        _ = AcceptLoop();
    }

    public string Url(string path = "/files/data.bin") => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}";

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var headers = await ReadHeaders(stream);
                headers.TryGetValue("range", out var range);
                Ranges.Enqueue(range);
                headers.TryGetValue("cookie", out var cookie);
                Cookies.Enqueue(cookie);
                var path = headers["__path"];
                if (range != "bytes=0-0") // the probe isn't a download
                {
                    ActivePaths.AddOrUpdate(path, 1, (_, n) => n + 1);
                    lock (ActivePaths) MaxActiveFiles = Math.Max(MaxActiveFiles, ActivePaths.Count(kv => kv.Value > 0));
                }
                try
                {

                long start = 0, end = Body.Length - 1;
                var partial = false;
                if (range is not null && SupportRanges && !IgnoreRangeWith200)
                {
                    var spec = range["bytes=".Length..].Split('-');
                    start = long.Parse(spec[0]);
                    if (spec[1].Length > 0) end = Math.Min(long.Parse(spec[1]), Body.Length - 1);
                    if (start >= Body.Length)
                    {
                        await Write(stream, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{Body.Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    }
                    partial = true;
                }

                var sb = new StringBuilder(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                sb.Append("Content-Type: application/octet-stream\r\nConnection: close\r\n");
                sb.Append($"ETag: {Etag}\r\n");
                if (SupportRanges) sb.Append("Accept-Ranges: bytes\r\n");
                if (partial) sb.Append($"Content-Range: bytes {start}-{end}/{Body.Length}\r\n");
                if (!Chunkless) sb.Append($"Content-Length: {end - start + 1}\r\n");
                if (ContentDisposition is not null) sb.Append($"Content-Disposition: {ContentDisposition}\r\n");
                sb.Append("\r\n");
                await Write(stream, sb.ToString());

                var limit = end - start + 1;
                var drop = DropAfterBytes > 0 && limit > DropAfterBytes && Interlocked.Decrement(ref _dropsLeft) >= 0;
                if (drop) limit = DropAfterBytes;
                for (long off = 0; off < limit; off += 65536)
                {
                    var n = (int)Math.Min(65536, limit - off);
                    await stream.WriteAsync(Body.AsMemory((int)(start + off), n));
                    var delay = SlowThrottleMs > 0 && start + off >= SlowFrom ? SlowThrottleMs : ThrottleMs;
                    if (delay > 0) await Task.Delay(delay);
                }
                await stream.FlushAsync();
                if (drop) client.Client.LingerState = new LingerOption(true, 0); // reset, not a clean close
                }
                finally
                {
                    if (range != "bytes=0-0") ActivePaths.AddOrUpdate(path, 0, (_, n) => n - 1);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    private static async Task<Dictionary<string, string>> ReadHeaders(NetworkStream s)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await s.ReadAsync(one) == 0) break;
            buf.Add(one[0]);
            if (buf.Count >= 4 && buf[^1] == '\n' && buf[^2] == '\r' && buf[^3] == '\n' && buf[^4] == '\r') break;
        }
        var lines = Encoding.ASCII.GetString(buf.ToArray()).Split("\r\n");
        var d = lines.Skip(1).Where(l => l.Contains(':'))
            .ToDictionary(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());
        d["__path"] = lines[0].Split(' ').ElementAtOrDefault(1) ?? "/";
        return d;
    }

    private static Task Write(NetworkStream s, string text) => s.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
