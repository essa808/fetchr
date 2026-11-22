using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;

namespace Fetchr.Core;

public static class FileNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Name from Content-Disposition, else the last URL path segment, else "download".</summary>
    public static string FromResponse(ContentDispositionHeaderValue? cd, Uri finalUri, string? contentType)
    {
        string? name = cd?.FileNameStar ?? cd?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(name))
        {
            var last = finalUri.Segments.LastOrDefault() ?? "";
            name = Uri.UnescapeDataString(last.TrimEnd('/'));
        }
        name = Sanitize(name);
        if (name.Length == 0) name = "download";
        if (Path.GetExtension(name).Length == 0 && GuessExtension(contentType) is { } ext) name += ext;
        return name;
    }

    /// <summary>A name that is safe as a single Windows file name component.</summary>
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length > 200) s = s[..(200 - Path.GetExtension(s).Length)] + Path.GetExtension(s);
        var stem = Path.GetFileNameWithoutExtension(s);
        if (Reserved.Contains(stem)) s = "_" + s;
        return s;
    }

    /// <summary>"name.zip" → "name (1).zip" … until neither the disk nor <paramref name="taken"/> has it.</summary>
    public static string Unique(string folder, string fileName, Func<string, bool>? taken = null)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = fileName;
        for (var i = 1; Exists(candidate); i++) candidate = $"{stem} ({i}){ext}";
        return candidate;

        bool Exists(string n) =>
            File.Exists(Path.Combine(folder, n)) || File.Exists(Path.Combine(folder, n + ".fetchr.part"))
            || Directory.Exists(Path.Combine(folder, n)) || (taken?.Invoke(n) ?? false);
    }

    private static string? GuessExtension(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "application/zip" or "application/x-zip-compressed" => ".zip",
        "application/pdf" => ".pdf",
        "video/mp4" => ".mp4",
        "audio/mpeg" => ".mp3",
        "application/x-msdownload" => ".exe",
        _ => null,
    };
}

public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Downloads { get; } = Resolve();

    public static string Expand(string path) =>
        path.Replace("%DOWNLOADS%", Downloads, StringComparison.OrdinalIgnoreCase);

    private static string Resolve()
    {
        if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) == 0)
        {
            try { return Marshal.PtrToStringUni(p)!; }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}

/// <summary>
/// Tags files as downloaded from the internet (Zone.Identifier stream), the same as browsers do,
/// so SmartScreen and Office treat downloaded programs and documents with care.
/// </summary>
public static class MarkOfTheWeb
{
    public static void Apply(string path, string? hostUrl, string? referrer)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var sb = new StringBuilder("[ZoneTransfer]\r\nZoneId=3\r\n");
            if (Safe(referrer) is { } r) sb.Append("ReferrerUrl=").Append(r).Append("\r\n");
            if (Safe(hostUrl) is { } h) sb.Append("HostUrl=").Append(h).Append("\r\n");
            File.WriteAllText(path + ":Zone.Identifier", sb.ToString());
        }
        catch (IOException) { /* FAT32 and some network drives have no alternate streams. */ }
        catch (UnauthorizedAccessException) { }
    }

    public static string? Read(string path)
    {
        try { return File.ReadAllText(path + ":Zone.Identifier"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // Only http(s) URLs, no credentials, no line breaks.
    private static string? Safe(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? new UriBuilder(u) { UserName = "", Password = "" }.Uri.AbsoluteUri
            : null;
}
