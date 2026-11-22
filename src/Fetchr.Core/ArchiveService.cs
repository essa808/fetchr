namespace Fetchr.Core;

public enum ExtractState { Done, WaitingParts, NeedsPassword, WrongPassword, NeedsSevenZip, Failed }

public sealed record ExtractOutcome(ExtractState State, string? Target = null, string? Message = null, int SkippedUnsafe = 0, string? SavedPasswordId = null);

/// <summary>
/// Unpacks one archive set the way WinRAR's "Extract to folder" does, with the safety checks first:
/// every entry path is validated (Zip Slip), the expanded size must fit on the disk and must not be an
/// absurd ratio of the packed size (zip bombs), and a missing volume means "wait", not "fail".
/// </summary>
public sealed class ArchiveService(Store store, Func<SevenZip?> findSevenZip)
{
    public int MaxRatio { get; init; } = 200;

    public async Task<ExtractOutcome> ExtractSetAsync(string folder, ArchiveInfo info, string? password,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var first = Path.Combine(folder, info.FirstPart);
        if (!File.Exists(first)) return new(ExtractState.WaitingParts, Message: $"Waiting for {info.FirstPart}.");

        // Plain zips use the built-in reader (no dependency). Encrypted or split zips fall through to 7-Zip.
        if (!info.NeedsSevenZip)
        {
            var target = Extractor.DefaultTarget(first);
            try
            {
                var r = await Task.Run(() => Extractor.ExtractZip(first, target, MaxRatio, progress, ct), ct);
                return new(ExtractState.Done, target, SkippedUnsafe: r.SkippedUnsafe);
            }
            catch (ExtractException e) when (e.InnerException is NotSupportedException && findSevenZip() is not null)
            {
                TryDeleteTree(target);
            }
            catch (ExtractException e)
            {
                TryDeleteTree(target);
                return new(ExtractState.Failed, Message: e.Message);
            }
        }

        if (findSevenZip() is not { } sz)
            return new(ExtractState.NeedsSevenZip, Message: "Install 7-Zip (free, 7-zip.org) to unpack .rar, .7z and split archives.");

        // The password the user just typed, or else no password followed by each saved one.
        List<(string? Id, string? Pwd)> candidates = password is not null
            ? [(null, password)]
            : [(null, null), .. store.ArchivePasswords().Select(p => ((string?)p.Id, Secrets.Unprotect(p.Secret))).Where(p => p.Item2 is not null)];

        var sawPassword = false;
        foreach (var (id, pwd) in candidates)
        {
            var list = await sz.ListAsync(first, pwd, ct);
            switch (list.Outcome)
            {
                case SevenZip.Outcome.MissingVolume:
                    return new(ExtractState.WaitingParts, Message: "Waiting for the other parts of this archive.");
                case SevenZip.Outcome.WrongPassword:
                    sawPassword = true;
                    continue; // encrypted names: try the next password
                case SevenZip.Outcome.Damaged:
                case SevenZip.Outcome.Failed:
                    return new(ExtractState.Failed, Message: "The archive is damaged or incomplete. " + list.Message);
            }
            if (pwd is null && list.Entries.Any(e => e.Encrypted))
            {
                sawPassword = true;
                continue; // file contents are encrypted: no point extracting without a password
            }

            var target = Extractor.UniqueFolder(folder, info.TargetName);
            var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var unsafeEntries = list.Entries.Where(e => Extractor.SafeDestination(root, e.Path) is null).Select(e => e.Path).ToList();

            var total = list.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
            var packed = Math.Max(1, Directory.EnumerateFiles(folder).Where(f => Archives.SameSet(Path.GetFileName(f), info) || f == first).Sum(f => new FileInfo(f).Length));
            if (total / packed > MaxRatio && !Archives.IsCompressedTar(info.FirstPart))
                return new(ExtractState.Failed, Message: $"This archive would expand {total / packed}× its size ({Bytes.Format(total)}). That's typical of a zip bomb, so it wasn't extracted.");
            var free = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
            if (total > free)
                return new(ExtractState.Failed, Message: $"Not enough space: needs {Bytes.Format(total)}, {Bytes.Format(free)} free.");

            Directory.CreateDirectory(target);
            var result = await sz.ExtractAsync(first, target, unsafeEntries, pwd, progress, ct);
            switch (result.Outcome)
            {
                case SevenZip.Outcome.Ok:
                    if (Archives.IsCompressedTar(info.FirstPart))
                    {
                        var inner = await UnpackInnerTarAsync(sz, target, ct);
                        if (inner is not null) return inner;
                    }
                    ApplyMarkOfTheWeb(first, target);
                    return new(ExtractState.Done, target, SkippedUnsafe: unsafeEntries.Count, SavedPasswordId: id);
                case SevenZip.Outcome.WrongPassword:
                    sawPassword = true;
                    TryDeleteTree(target);
                    continue;
                case SevenZip.Outcome.MissingVolume:
                    TryDeleteTree(target);
                    return new(ExtractState.WaitingParts, Message: "Waiting for the other parts of this archive.");
                default:
                    TryDeleteTree(target);
                    return new(ExtractState.Failed, Message: "The archive is damaged or incomplete. " + result.Message);
            }
        }

        return password is not null
            ? new(ExtractState.WrongPassword, Message: "Wrong password.")
            : sawPassword ? new(ExtractState.NeedsPassword, Message: "This archive is password-protected.")
            : new(ExtractState.Failed, Message: "Couldn't extract this archive.");
    }

    /// <summary>data.tar.gz → data.tar in the target → its contents in the target; the .tar is removed.</summary>
    private async Task<ExtractOutcome?> UnpackInnerTarAsync(SevenZip sz, string target, CancellationToken ct)
    {
        var tars = Directory.GetFiles(target, "*.tar");
        if (tars.Length != 1 || Directory.GetFileSystemEntries(target).Length != 1) return null;
        var tar = tars[0];
        var list = await sz.ListAsync(tar, null, ct);
        var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var unsafeEntries = list.Entries.Where(e => Extractor.SafeDestination(root, e.Path) is null).Select(e => e.Path).ToList();
        var r = await sz.ExtractAsync(tar, target, unsafeEntries, null, null, ct);
        if (r.Outcome != SevenZip.Outcome.Ok) return new(ExtractState.Failed, Message: "The inner .tar couldn't be unpacked. " + r.Message);
        File.Delete(tar);
        return null;
    }

    private static void ApplyMarkOfTheWeb(string archive, string target)
    {
        if (MarkOfTheWeb.Read(archive) is not { } motw) return;
        foreach (var f in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            try { File.WriteAllText(f + ":Zone.Identifier", motw); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // We only ever delete folders this run created and that hold nothing but what 7-Zip just wrote.
    private static void TryDeleteTree(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

}
