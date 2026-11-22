namespace Fetchr.Core.Tests;

public sealed class IpcTests
{
    [Fact]
    public async Task Request_and_reply_round_trip_over_the_pipe()
    {
        var name = "Fetchr.test." + Guid.NewGuid().ToString("N")[..8];
        IpcMessage? received = null;
        await using var server = new IpcServer(name, m =>
        {
            received = m;
            return Task.FromResult(new IpcReply(true, Accepted: m.Type == "capture", Version: Ipc.Version));
        });

        var reply = await Ipc.SendAsync(name, new IpcMessage("capture", Url: "https://example.com/a.zip", Cookies: "s=1"), TimeSpan.FromSeconds(5));

        Assert.NotNull(reply);
        Assert.True(reply.Accepted);
        Assert.Equal("https://example.com/a.zip", received!.Url);
        Assert.Equal("s=1", received.Cookies);
    }

    [Fact]
    public async Task No_server_means_null_quickly_not_a_hang()
    {
        var started = DateTime.UtcNow;
        var reply = await Ipc.SendAsync("Fetchr.nobody." + Guid.NewGuid().ToString("N")[..8], new IpcMessage("ping"), TimeSpan.FromMilliseconds(500));
        Assert.Null(reply);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Pipe_name_depends_on_the_database_so_test_copies_never_answer_for_the_real_app()
    {
        Assert.NotEqual(Ipc.PipeName(@"C:\a\fetchr.db"), Ipc.PipeName(@"C:\b\fetchr.db"));
        Assert.Equal(Ipc.PipeName(@"C:\A\Fetchr.db"), Ipc.PipeName(@"c:\a\fetchr.db"));
    }

    [Fact]
    public void Only_allow_listed_browser_headers_are_kept()
    {
        var kept = RequestHeaders.Filter(new Dictionary<string, string>
        {
            ["cookie"] = "a=b", ["User-Agent"] = "UA", ["Host"] = "evil.example", ["X-Forwarded-For"] = "1.2.3.4", ["Authorization"] = "x\r\nInjected: 1",
        });
        Assert.Equal(["Cookie", "User-Agent"], kept.Keys.Order());
    }
}
