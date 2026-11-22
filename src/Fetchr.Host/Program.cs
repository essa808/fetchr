// Fetchr native messaging host. The browser starts it and talks over stdin/stdout:
// each message is a 4-byte little-endian length followed by that many bytes of UTF-8 JSON.
// It forwards each message to the running app over the per-user named pipe, starting the app if
// a download needs it, and writes the app's reply back. Nothing else may ever be written to stdout.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fetchr.Core;

var origin = args.FirstOrDefault() ?? "";
// The browser already restricts who may start us (allowed_origins); refuse anyone else as well.
if (!origin.Equals($"chrome-extension://{Ipc.ExtensionId}/", StringComparison.OrdinalIgnoreCase)
    && Environment.GetEnvironmentVariable("FETCHR_HOST_TEST") != "1")
    return 1;

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var pipe = Ipc.PipeName(Ipc.DbPath);

while (await ReadAsync(stdin) is { } json)
{
    IpcReply reply;
    try
    {
        var msg = JsonSerializer.Deserialize<IpcMessage>(json, Ipc.Json);
        reply = msg is null ? new IpcReply(false, Error: "empty") : await ForwardAsync(msg);
    }
    catch (JsonException)
    {
        reply = new IpcReply(false, Error: "bad message");
    }
    await WriteAsync(stdout, JsonSerializer.Serialize(reply, Ipc.Json));
}
return 0;

async Task<IpcReply> ForwardAsync(IpcMessage msg)
{
    var reply = await Ipc.SendAsync(pipe, msg, TimeSpan.FromSeconds(3));
    if (reply is not null) return reply;
    if (msg.Type is "ping" or "get_config") return new IpcReply(true, Running: false, Version: Ipc.Version);

    // A real download: start the app, then wait for it to come up.
    var app = Path.Combine(AppContext.BaseDirectory, "Fetchr.App.exe");
    if (!File.Exists(app)) return new IpcReply(false, Running: false, Error: "Fetchr.App.exe not found next to the host.");
    Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, Arguments = "--minimized" });
    for (var i = 0; i < 40; i++)
    {
        await Task.Delay(250);
        if (await Ipc.SendAsync(pipe, msg, TimeSpan.FromSeconds(2)) is { } r) return r;
    }
    return new IpcReply(false, Running: false, Error: "Fetchr didn't start in time.");
}

static async Task<string?> ReadAsync(Stream s)
{
    var len = new byte[4];
    if (!await ReadExactlyOrEndAsync(s, len)) return null;
    var n = BinaryPrimitives.ReadInt32LittleEndian(len);
    if (n <= 0 || n > Ipc.MaxMessageBytes) return null; // refuse absurd sizes rather than allocate them
    var body = new byte[n];
    return await ReadExactlyOrEndAsync(s, body) ? Encoding.UTF8.GetString(body) : null;
}

static async Task<bool> ReadExactlyOrEndAsync(Stream s, byte[] buf)
{
    var read = 0;
    while (read < buf.Length)
    {
        var n = await s.ReadAsync(buf.AsMemory(read));
        if (n == 0) return false;
        read += n;
    }
    return true;
}

static async Task WriteAsync(Stream s, string json)
{
    var body = Encoding.UTF8.GetBytes(json);
    var len = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(len, body.Length);
    await s.WriteAsync(len);
    await s.WriteAsync(body);
    await s.FlushAsync();
}
