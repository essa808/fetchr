namespace Fetchr.Core;

public static class DownloadStatus
{
    public const string Queued = "queued";
    public const string Downloading = "downloading";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Error = "error";
    public const string Extracting = "extracting";
    public const string Extracted = "extracted";
    public const string ExtractFailed = "extract_failed";
    public const string WaitingParts = "waiting_parts";
    public const string NeedsPassword = "needs_password";

    /// <summary>The file itself is fully downloaded (whatever happened to its extraction).</summary>
    public static bool IsFinished(string s) => s is Completed or Extracting or Extracted or ExtractFailed or WaitingParts or NeedsPassword;
}

/// <summary>A row of the downloads table. Mutable so the store can map it with Dapper.</summary>
public sealed class DownloadRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Url { get; set; } = "";
    public string? FinalUrl { get; set; }
    public string? Referer { get; set; }
    public string FileName { get; set; } = "";
    public string SavePath { get; set; } = "";
    public string? CategoryId { get; set; }
    public long? TotalSize { get; set; }
    public long DownloadedBytes { get; set; }
    public string Status { get; set; } = DownloadStatus.Queued;
    public bool Resumable { get; set; }
    public string? Etag { get; set; }
    public string? LastModified { get; set; }
    public int MaxConnections { get; set; } = 8;
    public string? LastError { get; set; }
    /// <summary>Browser cookies / user agent for this download, DPAPI-encrypted; cleared once it finishes.</summary>
    public byte[]? RequestHeadersDpapi { get; set; }
    public string CreatedAt { get; set; } = Clock.NowIso();
    public string? CompletedAt { get; set; }

    public string FullPath => Path.Combine(SavePath, FileName);
    public string PartPath => FullPath + ".fetchr.part";
}

public sealed class SegmentRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DownloadId { get; set; } = "";
    public long StartByte { get; set; }
    /// <summary>Inclusive. <see cref="UnknownEnd"/> while the size is not known.</summary>
    public long EndByte { get; set; }
    /// <summary>Next byte to write.</summary>
    public long CurrentByte { get; set; }
    public string Status { get; set; } = "pending";

    public const long UnknownEnd = long.MaxValue - 1;
    public bool IsDone => CurrentByte > EndByte;
}

/// <summary>What a HEAD/Range probe learned about a link before the download is created.</summary>
public sealed record ProbeResult(
    string Url,
    string FinalUrl,
    string FileName,
    long? Size,
    bool Resumable,
    string? Etag,
    string? LastModified,
    string? ContentType);

/// <summary>Live view of a download for the UI. Immutable, safe to hand across threads.</summary>
public sealed record DownloadSnapshot(
    string Id,
    string FileName,
    string FullPath,
    string Status,
    long? TotalSize,
    long Downloaded,
    double BytesPerSecond,
    int ActiveConnections,
    int Connections,
    bool Resumable,
    string? Error,
    string? ExtractedTo,
    IReadOnlyList<SegmentView> Segments,
    string CreatedAt,
    string? CategoryId = null);

public sealed record SegmentView(long Start, long End, long Current);

internal static class Clock
{
    public static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}
