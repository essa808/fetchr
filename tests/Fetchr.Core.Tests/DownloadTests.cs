using System.Security.Cryptography;

namespace Fetchr.Core.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fetchr-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _body = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 123);
    private readonly TestServer _server;

    public DownloadTests()
    {
        Directory.CreateDirectory(_dir);
        _server = new TestServer(_body);
    }

    private Store NewStore() => new(Path.Combine(_dir, "fetchr.db"));

    private DownloadManager NewManager(Store store) => new(store)
    {
        FlushInterval = TimeSpan.FromMilliseconds(100),
        RetryBaseDelay = TimeSpan.FromMilliseconds(20),
        ReadTimeout = TimeSpan.FromSeconds(5),
        MinSegmentSize = 256 * 1024,
    };

    private string Out => Path.Combine(_dir, "out");

    private async Task<DownloadRecord> DownloadAsync(DownloadManager m, string? url = null)
    {
        var probe = await m.ProbeAsync(url ?? _server.Url());
        var rec = m.Add(probe, Out);
        await m.WaitAsync(rec.Id);
        return m.Store.GetDownload(rec.Id)!;
    }

    private void AssertSameBytes(DownloadRecord rec)
    {
        Assert.Equal(DownloadStatus.Completed, rec.Status);
        Assert.Equal(SHA256.HashData(_body), SHA256.HashData(File.ReadAllBytes(rec.FullPath)));
        Assert.False(File.Exists(rec.PartPath));
    }

    [Fact]
    public async Task Splits_into_segments_and_reassembles_the_exact_bytes()
    {
        await using var m = NewManager(NewStore());
        var rec = await DownloadAsync(m);

        AssertSameBytes(rec);
        Assert.True(rec.Resumable);
        Assert.Equal(_body.Length, rec.TotalSize);
        Assert.Equal(8, m.Store.GetSegments(rec.Id).Count);
        Assert.Contains(_server.Ranges, r => r is not null && !r.StartsWith("bytes=0-"));
    }

    [Fact]
    public async Task Pause_then_resume_in_a_new_process_continues_from_saved_offsets()
    {
        _server.ThrottleMs = 100; // ~1 s for the whole file over 8 connections
        var store = NewStore();
        string id;
        long savedAtPause;
        await using (var m1 = NewManager(store))
        {
            var rec = m1.Add(await m1.ProbeAsync(_server.Url()), Out);
            id = rec.Id;
            await Task.Delay(400);
            await m1.PauseAsync(id);
            var paused = store.GetDownload(id)!;
            Assert.Equal(DownloadStatus.Paused, paused.Status);
            savedAtPause = paused.DownloadedBytes;
            Assert.InRange(savedAtPause, 1, _body.Length - 1);
        }

        // "Restart the app": a fresh manager over the same database.
        _server.ThrottleMs = 0;
        while (_server.Ranges.TryDequeue(out _)) { }
        await using var m2 = NewManager(new Store(Path.Combine(_dir, "fetchr.db")));
        Assert.Equal(0, m2.Recover());
        m2.Start(id);
        await m2.WaitAsync(id);

        AssertSameBytes(m2.Store.GetDownload(id)!);
        // Every byte saved before the pause is never asked for again.
        var requested = _server.Ranges.Sum(r =>
        {
            var spec = r!["bytes=".Length..].Split('-');
            return long.Parse(spec[1]) - long.Parse(spec[0]) + 1;
        });
        Assert.Equal(_body.Length - savedAtPause, requested);
    }

    [Fact]
    public async Task Interrupted_download_is_marked_paused_on_next_start()
    {
        var store = NewStore();
        var rec = new DownloadRecord { Url = _server.Url(), FileName = "x.bin", SavePath = Out, Status = DownloadStatus.Downloading, TotalSize = 10, Resumable = true };
        store.InsertDownload(rec, [new SegmentRecord { DownloadId = rec.Id, StartByte = 0, EndByte = 9, CurrentByte = 4 }]);

        await using var m = NewManager(store);
        Assert.Equal(1, m.Recover());
        Assert.Equal(DownloadStatus.Paused, store.GetDownload(rec.Id)!.Status);
    }

    [Fact]
    public async Task Server_without_range_support_uses_one_connection()
    {
        _server.SupportRanges = false;
        await using var m = NewManager(NewStore());
        var rec = await DownloadAsync(m);

        AssertSameBytes(rec);
        Assert.False(rec.Resumable);
        Assert.Single(m.Store.GetSegments(rec.Id));
    }

    [Fact]
    public async Task Server_that_answers_ranges_with_200_does_not_corrupt_the_file()
    {
        await using var m = NewManager(NewStore());
        var probe = await m.ProbeAsync(_server.Url());
        _server.IgnoreRangeWith200 = true; // behaves after the probe, like a CDN edge that drops Range
        var rec = m.Add(probe, Out);
        await m.WaitAsync(rec.Id);
        rec = m.Store.GetDownload(rec.Id)!;

        AssertSameBytes(rec);
        Assert.False(rec.Resumable);
    }

    [Fact]
    public async Task Dropped_connections_are_retried_and_resumed()
    {
        _server.DropAfterBytes = 100_000;
        _server.DropsLeft = 6;
        await using var m = NewManager(NewStore());
        var rec = await DownloadAsync(m);

        AssertSameBytes(rec);
        Assert.True(_server.DropsLeft <= 0);
    }

    [Fact]
    public async Task Unknown_size_downloads_learn_the_size_at_the_end()
    {
        _server.SupportRanges = false;
        _server.Chunkless = true;
        await using var m = NewManager(NewStore());
        var rec = await DownloadAsync(m);

        AssertSameBytes(rec);
        Assert.Equal(_body.Length, rec.TotalSize);
    }

    [Fact]
    public async Task Missing_file_is_an_error_not_a_retry_loop()
    {
        await using var m = NewManager(NewStore());
        var probe = await m.ProbeAsync(_server.Url());
        _server.Body = []; // now every range is past the end → 416, the server "lost" the file
        var rec = m.Add(probe, Out);
        await m.WaitAsync(rec.Id);
        Assert.Equal(DownloadStatus.Error, m.Store.GetDownload(rec.Id)!.Status);
    }

    [Fact]
    public async Task Name_comes_from_content_disposition_and_never_overwrites()
    {
        _server.ContentDisposition = "attachment; filename=\"report:final?.zip\"";
        await using var m = NewManager(NewStore());
        Directory.CreateDirectory(Out);
        File.WriteAllText(Path.Combine(Out, "report_final_.zip"), "already here");

        var probe = await m.ProbeAsync(_server.Url("/dl?id=7"));
        Assert.Equal("report_final_.zip", probe.FileName);
        var rec = m.Add(probe, Out);
        Assert.Equal("report_final_ (1).zip", rec.FileName);
        await m.WaitAsync(rec.Id);
        Assert.Equal("already here", File.ReadAllText(Path.Combine(Out, "report_final_.zip")));
    }

    [Fact]
    public async Task Downloaded_files_get_mark_of_the_web()
    {
        await using var m = NewManager(NewStore());
        var rec = await DownloadAsync(m);
        var motw = MarkOfTheWeb.Read(rec.FullPath);
        Assert.NotNull(motw);
        Assert.Contains("ZoneId=3", motw);
    }

    [Fact]
    public async Task Queue_runs_at_most_max_parallel_and_then_the_rest()
    {
        _server.ThrottleMs = 20;
        var store = NewStore();
        store.SetMaxParallel(2);
        await using var m = NewManager(store);
        var probe = await m.ProbeAsync(_server.Url());
        var recs = Enumerable.Range(1, 4).Select(i => m.Add(probe with { }, Out, $"file{i}.bin")).ToList();

        Assert.Equal(2, m.RunningCount);
        Assert.Equal(2, recs.Count(r => !m.IsRunning(r.Id) && store.GetDownload(r.Id)!.Status == DownloadStatus.Queued));
        for (var i = 0; i < 300 && recs.Any(r => store.GetDownload(r.Id)!.Status != DownloadStatus.Completed); i++) await Task.Delay(50);

        Assert.All(recs, r => AssertSameBytes(store.GetDownload(r.Id)!));
        Assert.True(_server.MaxActiveFiles <= 2, $"at most 2 files at once, saw {_server.MaxActiveFiles}");
    }

    [Fact]
    public async Task Pausing_a_queued_download_takes_it_out_of_the_queue()
    {
        _server.ThrottleMs = 50;
        var store = NewStore();
        store.SetMaxParallel(1);
        await using var m = NewManager(store);
        var probe = await m.ProbeAsync(_server.Url());
        var first = m.Add(probe, Out, "a.bin");
        var second = m.Add(probe, Out, "b.bin");
        await m.PauseAsync(second.Id);
        await m.PauseAsync(first.Id);
        await Task.Delay(300);
        Assert.Equal(DownloadStatus.Paused, store.GetDownload(second.Id)!.Status);
        Assert.False(m.IsRunning(second.Id));
    }

    [Fact]
    public async Task Browser_cookies_are_sent_encrypted_at_rest_and_dropped_when_done()
    {
        _server.ThrottleMs = 30;
        var store = NewStore();
        await using var m = NewManager(store);
        var headers = new Dictionary<string, string> { ["Cookie"] = "session=abc123", ["X-Evil"] = "nope" };
        var probe = await m.ProbeAsync(_server.Url(), headers: headers);
        var rec = m.Add(probe, Out, headers: headers);

        var blob = store.GetDownload(rec.Id)!.RequestHeadersDpapi;
        Assert.NotNull(blob);
        Assert.DoesNotContain("abc123", System.Text.Encoding.UTF8.GetString(blob));

        await m.WaitAsync(rec.Id);
        AssertSameBytes(store.GetDownload(rec.Id)!);
        Assert.All(_server.Cookies, c => Assert.Equal("session=abc123", c));
        Assert.Null(store.GetDownload(rec.Id)!.RequestHeadersDpapi);
    }

    [Fact]
    public async Task Probe_rejects_non_http_links()
    {
        await using var m = NewManager(NewStore());
        await Assert.ThrowsAsync<ProbeException>(() => m.ProbeAsync("file:///C:/Windows/win.ini"));
    }

    public void Dispose()
    {
        _server.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
