using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fetchr.Core;

/// <summary>What the browser extension (through the native host) or a second app instance asks for.</summary>
public sealed record IpcMessage(
    string Type,                 // "capture" | "ping" | "get_config" | "show"
    string? Url = null,
    string? FinalUrl = null,
    string? Referrer = null,
    string? FileName = null,
    string? Mime = null,
    long? Size = null,
    string? Cookies = null,
    string? UserAgent = null);

public sealed record IpcReply(
    bool Ok,
    bool Accepted = false,
    bool Running = true,
    string? Version = null,
    string? Error = null,
    string[]? Types = null,
    long MinSize = 0);

/// <summary>
/// One request, one reply, as single JSON lines over a named pipe that only the current Windows user
/// can open (PipeOptions.CurrentUserOnly). No network port is ever opened.
/// </summary>
public static class Ipc
{
    public const string HostName = "com.fetchr.host";
    public const string ExtensionId = "ndkcffdgecpchigbnlgplnphjeijighb";
    public const string Version = "0.2.0";
    public const int MaxMessageBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Per database, so a test copy of the app never answers for the real one.</summary>
    public static string PipeName(string dbPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dbPath).ToLowerInvariant())))[..12];
        return $"Fetchr.{hash}";
    }

    public static string DbPath => Environment.GetEnvironmentVariable("FETCHR_DB") ?? Store.DefaultPath;

    // Library code: never resume on the caller's UI thread (a blocked caller would deadlock).
    public static async Task<IpcReply?> SendAsync(string pipeName, IpcMessage message, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            await WriteLineAsync(pipe, JsonSerializer.Serialize(message, Json), cts.Token).ConfigureAwait(false);
            var line = await ReadLineAsync(pipe, cts.Token).ConfigureAwait(false);
            return line is null ? null : JsonSerializer.Deserialize<IpcReply>(line, Json);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static async Task WriteLineAsync(Stream s, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        await s.WriteAsync(bytes, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var buf = new MemoryStream();
        var one = new byte[1];
        while (buf.Length < MaxMessageBytes)
        {
            if (await s.ReadAsync(one, ct).ConfigureAwait(false) == 0) break;
            if (one[0] == '\n') return Encoding.UTF8.GetString(buf.ToArray());
            buf.WriteByte(one[0]);
        }
        return buf.Length > 0 ? Encoding.UTF8.GetString(buf.ToArray()) : null;
    }
}

/// <summary>The app side of the pipe: answers one request per connection, forever, until disposed.</summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public IpcServer(string pipeName, Func<IpcMessage, Task<IpcReply>> handle)
    {
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    var conn = pipe;
                    pipe = null;
                    _ = Task.Run(() => ServeAsync(conn, handle));
                }
                catch (OperationCanceledException) { }
                catch (IOException) { await Task.Delay(200); }
                finally
                {
                    if (pipe is not null) await pipe.DisposeAsync();
                }
            }
        });
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, Func<IpcMessage, Task<IpcReply>> handle)
    {
        await using (pipe)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var line = await Ipc.ReadLineAsync(pipe, timeout.Token);
                if (line is null) return;
                IpcReply reply;
                try
                {
                    var msg = JsonSerializer.Deserialize<IpcMessage>(line, Ipc.Json);
                    reply = msg is null ? new IpcReply(false, Error: "empty") : await handle(msg);
                }
                catch (JsonException)
                {
                    reply = new IpcReply(false, Error: "bad message");
                }
                await Ipc.WriteLineAsync(pipe, JsonSerializer.Serialize(reply, Ipc.Json), timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _loop; } catch (OperationCanceledException) { }
    }
}
