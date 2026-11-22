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

    public long Downloaded => _segs.Sum(s => Math.Max(0, s.Written));

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
        var segs = _segs;
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
                var workers = _segs.Where(s => !s.Done).Select(s => RunSegmentAsync(s, round)).ToList();
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
        _segs = [new SegmentState(seg)];
    }

    private async Task RunSegmentAsync(SegmentState s, CancellationTokenSource round)
    {
        var ct = round.Token;
        var attempts = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (!s.Done)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    s.Active = true;
                    await FetchRangeAsync(s, buffer, ct);
                    attempts = 0;
                }
                catch (RangeIgnoredException)
                {
                    round.Cancel(); // stop the sibling workers; RunAsync restarts with one connection
                    throw;
                }
                catch (Exception e) when (IsTransient(e, ct))
                {
                    s.Active = false;
                    if (++attempts > _m.RetryLimit) throw;
                    var delay = TimeSpan.FromMilliseconds(Math.Min(_m.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempts - 1), 15_000));
                    await Task.Delay(delay, ct);
                    if (!_rec.Resumable) s.Current = s.Start; // can't continue mid-file without Range
                }
            }
        }
        finally
        {
            s.Active = false;
            ArrayPool<byte>.Shared.Return(buffer);
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
        if (ranged && resp.StatusCode == HttpStatusCode.OK && !(s.Current == 0 && _segs.Count == 1))
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

    /// <summary>Capture offsets, flush the data they describe, then store them. Order matters.</summary>
    private void Persist()
    {
        lock (_persistLock)
        {
            if (_handle is null || _handle.IsClosed) return;
            var captured = _segs.Select(s => (s.Id, s.Start, Current: s.Current, End: s.End, s.StatusText)).ToList();
            RandomAccess.FlushToDisk(_handle);
            _m.Store.SaveProgress(_rec.Id, captured.Sum(c => c.Current - c.Start),
                captured.Select(c => (c.Id, c.Current, c.End, c.StatusText)));
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
