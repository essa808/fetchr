using System.Collections.Concurrent;
using System.Net;

namespace Fetchr.Core;

/// <summary>The app's single entry point to downloading: probe, add, start, pause, remove, extract.</summary>
public sealed class DownloadManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly ConcurrentDictionary<string, double> _extractProgress = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _setLocks = new();
    private readonly Lock _slots = new();
    private readonly ArchiveService _archives;
    private bool _disposing;

    public DownloadManager(Store store, HttpClient? http = null, Func<SevenZip?>? findSevenZip = null)
    {
        Store = store;
        Http = http ?? CreateHttpClient();
        _archives = new ArchiveService(store, findSevenZip ?? SevenZip.Find) { MaxRatio = store.GetInt("extract_ratio_limit", 200) };
    }

    internal Store Store { get; }
    internal HttpClient Http { get; }

    // Tunables; tests shorten them.
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public int RetryLimit { get; init; } = 8;
    public long MinSegmentSize { get; init; } = 512 * 1024;
    /// <summary>Smallest tail a finished connection may take from a slow range.</summary>
    public long StealChunkMin { get; init; } = 1024 * 1024;
    public int MaxDynamicSegments { get; init; } = 64;

    /// <summary>Something about this download changed (status, name). Raised on a worker thread.</summary>
    public event Action<string>? Changed;

    /// <summary>A download finished writing its file. Raised on a worker thread.</summary>
    public event Action<string>? Finished;

    /// <summary>An archive set finished unpacking (or needs a password). Raised on a worker thread.</summary>
    public event Action<string, ExtractOutcome>? Extracted;

    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None, // byte offsets must match the file on the server
            MaxConnectionsPerServer = 32,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false, // cookies come per download from the browser, never shared between downloads
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Fetchr/0.2 (Windows)");
        return http;
    }

    public async Task<ProbeResult> ProbeAsync(string url, string? referer = null, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await Prober.ProbeAsync(Http, url, referer, headers, timeout.Token);
    }

    /// <summary>The folder a probed file would go to, from its category.</summary>
    public string SuggestFolder(string fileName) => Store.ResolveCategory(fileName).Folder;

    /// <summary>
    /// Create a download. With <paramref name="start"/> it takes a free slot or waits in the queue;
    /// without, it is added paused.
    /// </summary>
    public DownloadRecord Add(ProbeResult p, string? folder = null, string? fileName = null, bool start = true,
        string? referer = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var (categoryId, categoryFolder) = Store.ResolveCategory(fileName ?? p.FileName);
        folder = Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? categoryFolder : folder);
        Directory.CreateDirectory(folder);
        var name = FileNames.Unique(folder, FileNames.Sanitize(fileName ?? p.FileName), n => Store.TargetTaken(folder, n));

        var rec = new DownloadRecord
        {
            Url = p.Url,
            FinalUrl = p.FinalUrl,
            Referer = referer,
            FileName = name,
            SavePath = folder,
            CategoryId = categoryId,
            TotalSize = p.Size,
            Resumable = p.Resumable,
            Etag = p.Etag,
            LastModified = p.LastModified,
            MaxConnections = Math.Clamp(Store.GetInt("default_connections", 8), 1, 32),
            Status = start ? DownloadStatus.Queued : DownloadStatus.Paused,
            RequestHeadersDpapi = Secrets.ProtectHeaders(RequestHeaders.Filter(headers)),
        };
        Store.InsertDownload(rec, PlanSegments(rec, MinSegmentSize));
        Changed?.Invoke(rec.Id);
        if (start) Start(rec.Id);
        return rec;
    }

    internal static List<SegmentRecord> PlanSegments(DownloadRecord d, long minSegment)
    {
        if (d.TotalSize == 0) return [];
        if (d.TotalSize is not long size || !d.Resumable || size < 2 * minSegment)
            return [new SegmentRecord { DownloadId = d.Id, StartByte = 0, EndByte = d.TotalSize - 1 ?? SegmentRecord.UnknownEnd, CurrentByte = 0 }];

        var n = (int)Math.Min(d.MaxConnections, size / minSegment);
        var chunk = size / n;
        return Enumerable.Range(0, n).Select(i => new SegmentRecord
        {
            DownloadId = d.Id,
            StartByte = i * chunk,
            EndByte = i == n - 1 ? size - 1 : (i + 1) * chunk - 1,
            CurrentByte = i * chunk,
        }).ToList();
    }

    // ---- queue: at most MaxParallel downloads run; the rest wait as "queued", oldest first ----

    public int RunningCount => _jobs.Count;

    /// <summary>Run now if a slot is free, otherwise wait in the queue.</summary>
    public void Start(string id)
    {
        var rec = Store.GetDownload(id);
        if (rec is null || DownloadStatus.IsFinished(rec.Status) || _jobs.ContainsKey(id)) return;
        lock (_slots)
        {
            if (_jobs.Count >= Store.MaxParallel())
            {
                if (rec.Status != DownloadStatus.Queued) Store.SetStatus(id, DownloadStatus.Queued);
                Changed?.Invoke(id);
                return;
            }
            Launch(rec);
        }
    }

    private void Launch(DownloadRecord rec)
    {
        var job = new DownloadJob(this, rec, Store.GetSegments(rec.Id));
        _jobs[rec.Id] = job;
        job.Begin();
    }

    /// <summary>Fill free slots from the queue. Called when a job ends, at startup, and when the limit changes.</summary>
    public void PumpQueue()
    {
        if (_disposing) return;
        lock (_slots)
        {
            var free = Store.MaxParallel() - _jobs.Count;
            if (free <= 0) return;
            foreach (var id in Store.QueuedIds().Where(id => !_jobs.ContainsKey(id)).Take(free))
                if (Store.GetDownload(id) is { } rec) Launch(rec);
        }
    }

    public async Task PauseAsync(string id)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            await job.StopAsync();
            return;
        }
        if (Store.GetDownload(id) is { Status: DownloadStatus.Queued })
        {
            Store.SetStatus(id, DownloadStatus.Paused);
            Changed?.Invoke(id);
        }
    }

    public Task WaitAsync(string id) => _jobs.TryGetValue(id, out var job) ? job.Completion : Task.CompletedTask;

    public bool IsRunning(string id) => _jobs.ContainsKey(id);

    /// <summary>Take it off the list. An unfinished .part file is ours and goes too; finished files stay.</summary>
    public async Task RemoveAsync(string id)
    {
        await PauseAsync(id);
        var rec = Store.GetDownload(id);
        if (rec is null) return;
        Store.RemoveDownload(id);
        try { File.Delete(rec.PartPath); } catch (IOException) { }
        Changed?.Invoke(id);
    }

    /// <summary>Called once at startup, before anything starts: interrupted work becomes paused, the queue resumes.</summary>
    public int Recover()
    {
        var n = Store.MarkInterruptedAsPaused();
        PumpQueue();
        return n;
    }

    public IReadOnlyList<DownloadSnapshot> Snapshot()
    {
        var rows = Store.ListDownloads();
        var list = new List<DownloadSnapshot>(rows.Count);
        foreach (var r in rows)
        {
            if (_jobs.TryGetValue(r.Id, out var job))
            {
                list.Add(job.Snapshot(r));
                continue;
            }
            var downloaded = r.Status == DownloadStatus.Extracting && _extractProgress.TryGetValue(r.Id, out var p)
                ? (long)(p * (r.TotalSize ?? 0))
                : r.DownloadedBytes;
            list.Add(new DownloadSnapshot(r.Id, r.FileName, r.FullPath, r.Status, r.TotalSize, downloaded, 0, 0, 0,
                r.Resumable, r.LastError, r.Status == DownloadStatus.Extracted ? Store.LastExtractTarget(r.Id) : null, [], r.CreatedAt, r.CategoryId));
        }
        return list;
    }

    // ---- extraction ----

    /// <summary>
    /// Unpack the archive set this download belongs to. Waits (status "waiting for parts") while other
    /// parts are still downloading or missing; asks for a password when none of the saved ones fit.
    /// </summary>
    public async Task<ExtractOutcome?> ExtractAsync(string id, string? password = null, bool rememberPassword = false)
    {
        var rec = Store.GetDownload(id);
        if (rec is null || Archives.Identify(rec.FileName) is not { } info || !File.Exists(rec.FullPath)) return null;

        var gate = _setLocks.GetOrAdd(rec.SavePath.ToLowerInvariant() + "|" + info.SetKey, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0)) return null; // this set is already being unpacked
        try
        {
            var members = Store.ListDownloads()
                .Where(d => string.Equals(d.SavePath, rec.SavePath, StringComparison.OrdinalIgnoreCase) && Archives.SameSet(d.FileName, info))
                .ToList();
            if (members.Any(d => !DownloadStatus.IsFinished(d.Status)))
            {
                SetAll([rec], DownloadStatus.WaitingParts, "Waiting for the other parts to finish downloading.");
                return new(ExtractState.WaitingParts);
            }

            SetAll(members, DownloadStatus.Extracting, null);
            var progress = new Progress<double>(p => { foreach (var m in members) _extractProgress[m.Id] = p; });
            ExtractOutcome outcome;
            try
            {
                outcome = await Task.Run(() => _archives.ExtractSetAsync(rec.SavePath, info, password, progress));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                outcome = new(ExtractState.Failed, Message: e.Message);
            }

            switch (outcome.State)
            {
                case ExtractState.Done:
                    Store.RecordExtractTarget(members.Select(m => m.Id), info.SetKey, info.Format, outcome.Target!);
                    SetAll(members, DownloadStatus.Extracted, outcome.SkippedUnsafe > 0 ? $"{outcome.SkippedUnsafe} unsafe entries were skipped." : null);
                    if (outcome.SavedPasswordId is { } used) Store.MarkPasswordUsed(used);
                    if (rememberPassword && password is not null) RememberPassword(password);
                    break;
                case ExtractState.WaitingParts:
                    SetAll(members, DownloadStatus.WaitingParts, outcome.Message);
                    break;
                case ExtractState.NeedsPassword:
                case ExtractState.WrongPassword:
                    SetAll(members, DownloadStatus.NeedsPassword, outcome.Message);
                    break;
                default:
                    SetAll(members, DownloadStatus.ExtractFailed, outcome.Message);
                    break;
            }
            Extracted?.Invoke(id, outcome);
            return outcome;
        }
        finally
        {
            foreach (var k in _extractProgress.Keys) _extractProgress.TryRemove(k, out _);
            gate.Release();
        }
    }

    private void SetAll(IEnumerable<DownloadRecord> members, string status, string? message)
    {
        foreach (var m in members)
        {
            Store.SetStatus(m.Id, status, message);
            Changed?.Invoke(m.Id);
        }
    }

    private void RememberPassword(string password)
    {
        if (Store.ArchivePasswords().Any(p => Secrets.Unprotect(p.Secret) == password)) return;
        Store.AddArchivePassword(Secrets.Protect(password));
    }

    // ---- callbacks from jobs ----

    internal void RaiseChanged(string id) => Changed?.Invoke(id);

    internal void OnFinished(string id)
    {
        Finished?.Invoke(id);
        var rec = Store.GetDownload(id);
        if (rec is null || !Store.GetBool("auto_extract", true) || Archives.Identify(rec.FileName) is not { } info) return;
        _ = ExtractAsync(id);
        // A part of a set finishing may complete a set another part was waiting on.
        foreach (var waiting in Store.ListDownloads().Where(d => d.Id != id && d.Status == DownloadStatus.WaitingParts
                     && string.Equals(d.SavePath, rec.SavePath, StringComparison.OrdinalIgnoreCase) && Archives.SameSet(d.FileName, info)))
            _ = ExtractAsync(waiting.Id);
    }

    internal void JobEnded(DownloadJob job)
    {
        _jobs.TryRemove(job.Id, out _);
        Changed?.Invoke(job.Id);
        PumpQueue();
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        await Task.WhenAll(_jobs.Values.Select(j => j.StopAsync()));
        Http.Dispose();
    }
}
