using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace Fetchr.Core;

internal sealed class RangeIgnoredException() : Exception("The server ignored the byte range.");
internal sealed class TransientHttpException(HttpStatusCode code) : Exception($"Server answered {(int)code}.");
internal sealed class PermanentDownloadException(string message) : Exception(message);

/// <summary>Live state of one byte range. Written by its worker, read by the flusher and the UI.</summary>
internal sealed class SegmentState(SegmentRecord r)
{
    public string Id { get; } = r.Id;
    public long Start { get; set; } = r.StartByte;
    private long _end = r.EndByte;
    private long _current = r.CurrentByte;
    private int _active;

    public long End { get => Volatile.Read(ref _end); set => Volatile.Write(ref _end, value); }
    public long Current { get => Volatile.Read(ref _current); set => Volatile.Write(ref _current, value); }
    public bool Active { get => Volatile.Read(ref _active) == 1; set => Volatile.Write(ref _active, value ? 1 : 0); }
    public bool Done => Current > End;
    public long Written => Current - Start;
    public string StatusText => Done ? "done" : Active ? "active" : "pending";
}

/// <summary>
/// Runs one download: N workers each fetch their byte range with HTTP Range and write it
/// at its offset in a pre-sized .fetchr.part file. A flusher saves progress every couple of
/// seconds, always after the data itself is flushed, so a power cut costs seconds, not the file.
/// </summary>
internal sealed class DownloadJob
{
    private readonly DownloadManager _m;
    private readonly DownloadRecord _rec;
    private List<SegmentState> _segs;
    private readonly Lock _segsLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, string> _headers;
    private SafeFileHandle? _handle;

    private long _speedBytes;
    private DateTime _speedAt = DateTime.UtcNow;
    private double _speed;

    public DownloadJob(DownloadManager m, DownloadRecord rec, List<SegmentRecord> segs)
    {
        _m = m;
        _rec = rec;
        _segs = segs.Select(s => new SegmentState(s)).ToList();
        _headers = Secrets.UnprotectHeaders(rec.RequestHeadersDpapi);
    }

    public string Id => _rec.Id;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public void Begin() => Completion = Task.Run(RunAsync);

    public async Task StopAsync()
    {
        _stop.Cancel();
        try { await Completion; } catch { /* the job records its own outcome */ }
    }

    public long Downloaded
    {
        get { lock (_segsLock) return _segs.Sum(s => Math.Max(0, s.Written)); }
    }

    public DownloadSnapshot Snapshot(DownloadRecord stored)
    {
        var now = DateTime.UtcNow;
        var bytes = Downloaded;
        var dt = (now - _speedAt).TotalSeconds;
        if (dt >= 0.5)
        {
            var instant = Math.Max(0, bytes - _speedBytes) / dt;
            _speed = _speed == 0 ? instant : _speed * 0.6 + instant * 0.4;
            _speedBytes = bytes;
            _speedAt = now;
        }
        List<SegmentState> segs;
        lock (_segsLock) segs = _segs.ToList();
        return new DownloadSnapshot(_rec.Id, _rec.FileName, _rec.FullPath, DownloadStatus.Downloading, _rec.TotalSize, bytes, _speed,
            segs.Count(s => s.Active), segs.Count, _rec.Resumable, null, null,
            segs.Select(s => new SegmentView(s.Start, s.End, s.Current)).ToList(), stored.CreatedAt, stored.CategoryId);
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        _m.Store.SetStatus(_rec.Id, DownloadStatus.Downloading);
        _m.RaiseChanged(_rec.Id);
        Task? flusher = null;
        using var flusherStop = new CancellationTokenSource();
        try
        {
            Directory.CreateDirectory(_rec.SavePath);
            _handle = File.OpenHandle(_rec.PartPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);

            // A server that can't resume means starting over; never trust bytes from a previous attempt.
            if (!_rec.Resumable && Downloaded > 0) ResetToSingleSegment(_rec.TotalSize);
            if (_rec.TotalSize is long size && RandomAccess.GetLength(_handle) != size) RandomAccess.SetLength(_handle, size);

            flusher = FlushLoopAsync(flusherStop.Token);

            var restarted = false;
            while (true)
            {
                using var round = CancellationTokenSource.CreateLinkedTokenSource(ct);
                List<SegmentState> pending;
                lock (_segsLock) pending = _segs.Where(s => !s.Done).ToList();
                var workers = pending.Select(s => RunSegmentAsync(s, round)).ToList();
                try
                {
                    await Task.WhenAll(workers);
                    break;
                }
                catch when (workers.Any(w => w.Exception?.InnerException is RangeIgnoredException) && !restarted && !ct.IsCancellationRequested)
                {
                    // The server sent the whole file to a ranged request. Fall back to one plain connection.
                    restarted = true;
                    _rec.Resumable = false;
                    ResetToSingleSegment(_rec.TotalSize);
                    _m.Store.UpdateDownload(_rec);
                }
            }

            flusherStop.Cancel();
            await flusher;
            Persist();
            if (_rec.TotalSize is null)
            {
                _rec.TotalSize = _segs.Sum(s => s.Written);
                RandomAccess.SetLength(_handle, _rec.TotalSize.Value);
            }
            _handle.Dispose();
            Finish();
        }
        catch (Exception e)
        {
            flusherStop.Cancel();
            if (flusher is not null) await flusher.ContinueWith(_ => { });
            if (_handle is { IsClosed: false })
            {
                Persist();
                _handle.Dispose();
            }
            var paused = ct.IsCancellationRequested;
            _rec.DownloadedBytes = Downloaded;
            _rec.Status = paused ? DownloadStatus.Paused : DownloadStatus.Error;
            _rec.LastError = paused ? null : Describe(e);
            _m.Store.UpdateDownload(_rec);
        }
        finally
        {
            _m.JobEnded(this);
        }
    }

    private void Finish()
    {
        var folder = _rec.SavePath;
        if (File.Exists(_rec.FullPath))
            _rec.FileName = FileNames.Unique(folder, _rec.FileName);
        File.Move(_rec.PartPath, _rec.FullPath);
        MarkOfTheWeb.Apply(_rec.FullPath, _rec.FinalUrl ?? _rec.Url, _rec.Referer);

        _rec.DownloadedBytes = _rec.TotalSize ?? Downloaded;
        _rec.Status = DownloadStatus.Completed;
        _rec.LastError = null;
        _rec.CompletedAt = Clock.NowIso();
        _rec.RequestHeadersDpapi = null; // the browser session isn't needed any more
        _m.Store.UpdateDownload(_rec);
        _m.OnFinished(_rec.Id);
    }

    private void ResetToSingleSegment(long? size)
    {
        var seg = new SegmentRecord
        {
            DownloadId = _rec.Id,
            StartByte = 0,
            EndByte = size is long s ? s - 1 : SegmentRecord.UnknownEnd,
            CurrentByte = 0,
        };
        _m.Store.ReplaceSegments(_rec.Id, [seg]);
        lock (_segsLock) _segs = [new SegmentState(seg)];
    }

    private async Task RunSegmentAsync(SegmentState s, CancellationTokenSource round)
    {
        var ct = round.Token;
        var attempts = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var work = s;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    work.Active = true;
                    await FetchRangeAsync(work, buffer, ct);
                    attempts = 0;
                }
                catch (RangeIgnoredException)
                {
                    round.Cancel(); // stop the sibling workers; RunAsync restarts with one connection
                    throw;
                }
                catch (Exception e) when (IsTransient(e, ct))
                {
                    work.Active = false;
                    if (++attempts > _m.RetryLimit) throw;
                    var delay = TimeSpan.FromMilliseconds(Math.Min(_m.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempts - 1), 15_000));
                    await Task.Delay(delay, ct);
                    if (!_rec.Resumable) work.Current = work.Start; // can't continue mid-file without Range
                    continue;
                }
                work.Active = false;
                if (!work.Done) continue;
                // This connection is free: take the tail of the slowest remaining range instead of stopping.
                if (TrySteal(work) is not { } stolen) break;
                work = stolen;
            }
        }
        finally
        {
            work.Active = false;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Dynamic re-splitting: hand half of the largest unfinished range to a connection whose own
    /// range just finished, so one slow connection no longer holds up the whole file.
    /// </summary>
    private SegmentState? TrySteal(SegmentState finished)
    {
        if (!_rec.Resumable || _rec.TotalSize is not long) return null;
        lock (_segsLock)
        {
            if (_segs.Count >= _m.MaxDynamicSegments) return null;

            SegmentState? victim = null;
            long best = 0;
            foreach (var v in _segs)
            {
                if (ReferenceEquals(v, finished) || v.Done) continue;
                var remaining = v.End - v.Current + 1;
                if (remaining > best)
                {
                    best = remaining;
                    victim = v;
                }
            }
            if (victim is null || best < 2 * _m.StealChunkMin) return null;

            var take = best / 2;
            var oldEnd = victim.End;
            var takeStart = oldEnd - take + 1;

            var rec = new SegmentRecord
            {
                DownloadId = _rec.Id,
                StartByte = takeStart,
                EndByte = oldEnd,
                CurrentByte = takeStart,
            };
            // Store the new range before shrinking the victim: if anything fails here, the saved
            // state still covers every byte (an overlap at worst), never a hole.
            _m.Store.InsertSegment(rec);
            victim.End = takeStart - 1; // the rest of the range belongs to the thief now
            var stolen = new SegmentState(rec);
            _segs.Add(stolen);
            return stolen;
        }
    }

    private async Task FetchRangeAsync(SegmentState s, byte[] buffer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _rec.FinalUrl ?? _rec.Url);
        if (_rec.Referer is not null && Uri.TryCreate(_rec.Referer, UriKind.Absolute, out var referer)) req.Headers.Referrer = referer;
        RequestHeaders.Apply(req, _headers);
        var ranged = _rec.Resumable;
        if (ranged)
        {
            req.Headers.Range = new RangeHeaderValue(s.Current, s.End == SegmentRecord.UnknownEnd ? null : s.End);
            // If the file changed on the server, If-Range makes it send the whole new file (200), which we catch below.
            if (_rec.Etag is not null && EntityTagHeaderValue.TryParse(_rec.Etag, out var tag)) req.Headers.IfRange = new RangeConditionHeaderValue(tag);
        }

        using var resp = await _m.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (ranged && resp.StatusCode == HttpStatusCode.OK && !(s.Current == 0 && SegmentCount == 1))
            throw new RangeIgnoredException();
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && s.Current > s.End)
            return;
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            if (code is 408 or 429 or >= 500) throw new TransientHttpException(resp.StatusCode);
            throw new PermanentDownloadException(resp.StatusCode switch
            {
                HttpStatusCode.NotFound => "The file is gone from the server (404).",
                HttpStatusCode.Forbidden => "The server refused (403). The link may have expired.",
                _ => $"The server answered {code} {resp.ReasonPhrase}.",
            });
        }

        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            var remaining = s.End - s.Current + 1;
            if (remaining <= 0) break;
            readTimeout.CancelAfter(_m.ReadTimeout);
            var n = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), readTimeout.Token);
            if (n == 0) break;
            await RandomAccess.WriteAsync(_handle!, buffer.AsMemory(0, n), s.Current, ct);
            s.Current += n;
        }

        if (s.End == SegmentRecord.UnknownEnd)
        {
            s.End = s.Current - 1; // stream ended: now we know the size
            return;
        }
        if (!s.Done) throw new IOException("The connection closed before the range was complete.");
    }

    private static bool IsTransient(Exception e, CancellationToken ct) => e switch
    {
        OperationCanceledException => !ct.IsCancellationRequested, // read timeout, not a pause
        HttpRequestException or IOException or TransientHttpException => true,
        _ => false,
    };

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_m.FlushInterval, ct);
                Persist();
            }
        }
        catch (OperationCanceledException) { }
    }

    private readonly Lock _persistLock = new();

    private int SegmentCount
    {
        get { lock (_segsLock) return _segs.Count; }
    }

    /// <summary>Capture offsets, flush the data they describe, then store them. Order matters.</summary>
    private void Persist()
    {
        lock (_persistLock)
        {
            if (_handle is null || _handle.IsClosed) return;
            List<(string Id, long Start, long Current, long End, string Status)> captured;
            lock (_segsLock)
                captured = _segs.Select(s => (s.Id, s.Start, Current: s.Current, End: s.End, s.StatusText)).ToList();
            RandomAccess.FlushToDisk(_handle);
            _m.Store.SaveProgress(_rec.Id, captured.Sum(c => c.Current - c.Start),
                captured.Select(c => (c.Id, c.Current, c.End, c.Status)));
        }
    }

    private static string Describe(Exception e) => e switch
    {
        PermanentDownloadException p => p.Message,
        TransientHttpException t => t.Message + " Gave up after several tries.",
        HttpRequestException h => "Connection problem: " + h.Message,
        OperationCanceledException => "The server stopped sending data.",
        IOException io => io.Message,
        UnauthorizedAccessException => "Windows denied access to the save folder.",
        _ => e.Message,
    };
}
