using System.IO.Compression;

namespace Fetchr.Core;

public sealed class ExtractException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record ExtractResult(string Target, int Files, int SkippedUnsafe);

/// <summary>
/// Unpacks archives safely: nothing is written outside the target folder (Zip Slip),
/// reserved device names are skipped, and archives that would fill the disk or expand
/// absurdly (zip bombs) are refused before a single byte is written.
/// This is the built-in .zip path; everything else goes through <see cref="ArchiveService"/> and 7-Zip.
/// </summary>
public static class Extractor
{
    public static bool CanExtract(string fileName) => Fetchr.Core.Archives.Identify(fileName) is not null;

    /// <summary>"C:\dl\photos.zip" → "C:\dl\photos" (or "photos (1)" if taken).</summary>
    public static string DefaultTarget(string archivePath) =>
        UniqueFolder(Path.GetDirectoryName(archivePath)!, Path.GetFileNameWithoutExtension(archivePath));

    /// <summary>folder\name, or folder\name (1) … if something already has that name.</summary>
    public static string UniqueFolder(string folder, string name) => Path.Combine(folder, FileNames.Unique(folder, name));

    public static ExtractResult ExtractZip(string archivePath, string target, int maxRatio = 200,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            // The built-in reader would write encrypted bytes out as if they were the file. Never let it.
            if (zip.Entries.Any(e => e.IsEncrypted))
                throw new ExtractException("This archive is password-protected.", new NotSupportedException("encrypted entries"));
            var total = zip.Entries.Sum(e => e.Length);
            var packed = Math.Max(1, new FileInfo(archivePath).Length);

            if (total / packed > maxRatio)
                throw new ExtractException($"This archive would expand {total / packed}× its size ({Bytes.Format(total)}). That's typical of a zip bomb, so it wasn't extracted.");
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(target))!).AvailableFreeSpace;
            if (total > free)
                throw new ExtractException($"Not enough space: needs {Bytes.Format(total)}, {Bytes.Format(free)} free.");

            var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(root);
            var motw = MarkOfTheWeb.Read(archivePath);
            int files = 0, skipped = 0;
            long done = 0;

            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var dest = SafeDestination(root, entry.FullName);
                if (dest is null) { skipped++; continue; }

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest)) { skipped++; continue; } // duplicate entry names: keep the first
                entry.ExtractToFile(dest, overwrite: false);
                if (motw is not null) File.WriteAllText(dest + ":Zone.Identifier", motw);
                files++;
                done += entry.Length;
                progress?.Report(total == 0 ? 1 : (double)done / total);
            }
            return new ExtractResult(target, files, skipped);
        }
        catch (InvalidDataException e)
        {
            throw new ExtractException("The archive is damaged or incomplete.", e);
        }
        catch (NotSupportedException e)
        {
            throw new ExtractException("This archive is password-protected or uses a method the built-in extractor can't read yet.", e);
        }
    }

    /// <summary>The full path for an entry, or null when it would land outside <paramref name="root"/> or isn't a legal name.</summary>
    internal static string? SafeDestination(string root, string entryName)
    {
        var name = entryName.Replace('\\', '/');
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':')) return null;
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => FileNames.Sanitize(p) != p && p != ".." && p != ".")) return null;

        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        var asDir = full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!asDir.StartsWith(root, StringComparison.OrdinalIgnoreCase) || asDir.Equals(root, StringComparison.OrdinalIgnoreCase))
            return null;
        return full;
    }
}

public static class Bytes
{
    public static string Format(long? bytes)
    {
        if (bytes is not long b) return "?";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = b;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.##} {units[i]}";
    }
}
