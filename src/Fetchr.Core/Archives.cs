using System.Text.RegularExpressions;

namespace Fetchr.Core;

/// <summary>
/// What kind of archive a file name is, and which multi-part set it belongs to.
/// SetKey is the same for every part of one archive; FirstPart is the file 7-Zip must be given.
/// </summary>
public sealed record ArchiveInfo(string Format, string BaseName, string FirstPart, bool MultiPart, string SetKey)
{
    /// <summary>Folder name to extract into: "photos.part1.rar" → "photos", "data.tar.gz" → "data".</summary>
    public string TargetName => BaseName;
    public bool NeedsSevenZip => Format != "zip" || MultiPart;
}

public static partial class Archives
{
    // name.part01.rar (RAR 3+ volumes)
    [GeneratedRegex(@"^(?<base>.+)\.part(?<n>\d+)\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex RarPart();

    // name.7z.001, name.zip.001, name.001 (split files)
    [GeneratedRegex(@"^(?<base>.+?)(?<inner>\.(7z|zip|rar|tar|iso))?\.(?<n>\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex Numbered();

    // old-style RAR volumes: name.rar, name.r00, name.r01 …
    [GeneratedRegex(@"^(?<base>.+)\.r(?<n>\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex OldRar();

    // split zip: name.zip, name.z01, name.z02 …
    [GeneratedRegex(@"^(?<base>.+)\.z(?<n>\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitZip();

    private static readonly Dictionary<string, string> Single = new(StringComparer.OrdinalIgnoreCase)
    {
        [".zip"] = "zip", [".rar"] = "rar", [".7z"] = "7z", [".tar"] = "tar", [".iso"] = "iso",
        [".gz"] = "gz", [".tgz"] = "gz", [".xz"] = "xz", [".txz"] = "xz", [".bz2"] = "bz2", [".tbz2"] = "bz2",
        [".cab"] = "cab",
    };

    /// <summary>Null when the name isn't an archive we unpack.</summary>
    public static ArchiveInfo? Identify(string fileName)
    {
        var name = Path.GetFileName(fileName);
        Match m;
        if ((m = RarPart().Match(name)).Success)
        {
            var b = m.Groups["base"].Value;
            var width = m.Groups["n"].Value.Length;
            return new("rar", b, $"{b}.part{1.ToString().PadLeft(width, '0')}.rar", true, Key(b, "rar"));
        }
        if ((m = Numbered().Match(name)).Success)
        {
            var b = m.Groups["base"].Value;
            var inner = m.Groups["inner"].Value;
            var format = inner.Length > 0 ? inner.TrimStart('.').ToLowerInvariant() : "split";
            return new(format, b, $"{b}{inner}.001", true, Key(b + inner, "split"));
        }
        if ((m = OldRar().Match(name)).Success)
        {
            var b = m.Groups["base"].Value;
            return new("rar", b, b + ".rar", true, Key(b, "rar"));
        }
        if ((m = SplitZip().Match(name)).Success)
        {
            var b = m.Groups["base"].Value;
            return new("zip", b, b + ".zip", true, Key(b, "zip"));
        }

        var ext = Path.GetExtension(name);
        if (!Single.TryGetValue(ext, out var fmt)) return null;
        var stem = Path.GetFileNameWithoutExtension(name);
        // "data.tar.gz" unpacks to a folder called "data", like "data.tgz".
        if (fmt is "gz" or "xz" or "bz2" && stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        // A plain .rar or .zip may still be the first volume of an old-style set (.r00 / .z01 next to it);
        // they share the key so the set is handled once.
        return new(fmt, stem, name, false, Key(stem, fmt is "rar" or "zip" ? fmt : fmt + ":" + name));
    }

    /// <summary>Is this file one of the parts of <paramref name="set"/>?</summary>
    public static bool SameSet(string fileName, ArchiveInfo set) =>
        Identify(fileName) is { } other && other.SetKey == set.SetKey;

    /// <summary>A gzip/xz/bzip2 stream whose content is a tar: unpack twice.</summary>
    public static bool IsCompressedTar(string fileName)
    {
        var lower = fileName.ToLowerInvariant();
        return lower.EndsWith(".tgz") || lower.EndsWith(".txz") || lower.EndsWith(".tbz2")
            || lower.EndsWith(".tar.gz") || lower.EndsWith(".tar.xz") || lower.EndsWith(".tar.bz2");
    }

    private static string Key(string baseName, string family) => (family + "|" + baseName).ToLowerInvariant();
}
