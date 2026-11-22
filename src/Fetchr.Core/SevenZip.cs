using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Fetchr.Core;

/// <summary>
/// Runs 7-Zip's command-line tool (7z.exe, LGPL; RAR decoding under the unRAR licence: extract only).
/// 7-Zip is looked for next to Fetchr first (the installer will bundle it), then where 7-Zip installs itself.
/// </summary>
public sealed partial class SevenZip(string exePath)
{
    public string ExePath { get; } = exePath;

    public enum Outcome { Ok, MissingVolume, WrongPassword, Damaged, Failed }

    public sealed record Entry(string Path, long Size, bool IsDirectory, bool Encrypted);

    public sealed record Result(Outcome Outcome, string Message, IReadOnlyList<Entry> Entries);

    public static SevenZip? Find()
    {
        var candidates = new List<string?> { Path.Combine(AppContext.BaseDirectory, "7-Zip", "7z.exe") };
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\7-Zip");
            candidates.Add(key?.GetValue("Path64") as string is { } p64 ? Path.Combine(p64, "7z.exe") : null);
            candidates.Add(key?.GetValue("Path") as string is { } p ? Path.Combine(p, "7z.exe") : null);
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"));
        return candidates.FirstOrDefault(File.Exists) is { } exe ? new SevenZip(exe) : null;
    }

    public async Task<Result> ListAsync(string archive, string? password, CancellationToken ct = default)
    {
        // No -ba: archive-level errors such as "ERROR = Missing volume : x.part2.rar" only appear in the header block.
        var (code, stdout, stderr) = await RunAsync(["l", "-slt", "-sccUTF-8", Pwd(password), "--", archive], null, ct);
        var outcome = Classify(code, stdout + "\n" + stderr);
        return new Result(outcome, Message(stderr, stdout), outcome == Outcome.Ok ? ParseList(stdout) : []);
    }

    /// <summary>Extract everything except <paramref name="exclude"/> (exact archive paths) into <paramref name="target"/>.</summary>
    public async Task<Result> ExtractAsync(string archive, string target, IReadOnlyCollection<string> exclude, string? password,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string? listFile = null;
        try
        {
            // Without -spf, 7-Zip never writes outside the output folder; the exclude list removes the
            // entries we already judged unsafe, so they are not even attempted.
            List<string> args = ["x", "-y", "-aos", "-bso0", "-bsp1", "-sccUTF-8", "-scsUTF-8", "-o" + target, Pwd(password)];
            if (exclude.Count > 0)
            {
                listFile = Path.GetTempFileName();
                await File.WriteAllLinesAsync(listFile, exclude, new UTF8Encoding(false), ct);
                args.Add("-x@" + listFile);
            }
            args.AddRange(["--", archive]);
            var (code, stdout, stderr) = await RunAsync(args, chunk =>
            {
                var m = Percent().Matches(chunk);
                if (m.Count > 0) progress?.Report(int.Parse(m[^1].Groups[1].Value) / 100.0);
            }, ct);
            return new Result(Classify(code, stdout + "\n" + stderr), Message(stderr, stdout), []);
        }
        finally
        {
            if (listFile is not null) File.Delete(listFile);
        }
    }

    // "-p" with an empty value means "no password" and stops 7-Zip from prompting on the console.
    private static string Pwd(string? password) => "-p" + (password ?? "");

    internal static Outcome Classify(int exitCode, string output)
    {
        if (output.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Cannot open encrypted archive", StringComparison.OrdinalIgnoreCase))
            return Outcome.WrongPassword;
        if (output.Contains("Missing volume", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Unexpected end of archive", StringComparison.OrdinalIgnoreCase))
            return Outcome.MissingVolume;
        if (exitCode is 0 or 1) return Outcome.Ok;
        if (output.Contains("Data Error", StringComparison.OrdinalIgnoreCase) || output.Contains("CRC Failed", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Headers Error", StringComparison.OrdinalIgnoreCase) || output.Contains("Cannot open the file as", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Is not archive", StringComparison.OrdinalIgnoreCase))
            return Outcome.Damaged;
        return Outcome.Failed;
    }

    internal static List<Entry> ParseList(string slt)
    {
        var entries = new List<Entry>();
        slt = slt.Replace("\r", "");
        // Entries follow the "----------" line; everything before it describes the archive itself.
        var sep = slt.IndexOf("\n----------\n", StringComparison.Ordinal);
        if (sep >= 0) slt = slt[(sep + 12)..];
        foreach (var block in slt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var f = block.Split('\n').Select(l => l.Split(" = ", 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
            if (!f.TryGetValue("Path", out var path)) continue;
            var isDir = f.GetValueOrDefault("Folder") == "+" || (f.GetValueOrDefault("Attributes") ?? "").StartsWith('D');
            entries.Add(new Entry(path, long.TryParse(f.GetValueOrDefault("Size"), out var size) ? size : 0, isDir, f.GetValueOrDefault("Encrypted") == "+"));
        }
        return entries;
    }

    private static string Message(string stderr, string stdout)
    {
        var lines = (stderr + "\n" + stdout).Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || l.Contains(" : ")).ToList();
        return lines.Count > 0 ? string.Join(" ", lines.Take(2)) : stderr.Trim();
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(IEnumerable<string> args, Action<string>? onChunk, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start 7-Zip.");
        p.StandardInput.Close(); // never wait on a console prompt
        var stdout = new StringBuilder();
        var outTask = Task.Run(async () =>
        {
            var buf = new char[4096];
            int n;
            while ((n = await p.StandardOutput.ReadAsync(buf, ct)) > 0)
            {
                var chunk = new string(buf, 0, n);
                stdout.Append(chunk);
                onChunk?.Invoke(chunk);
            }
        }, ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try
        {
            await p.WaitForExitAsync(ct);
            await outTask;
            return (p.ExitCode, stdout.ToString(), await errTask);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex Percent();
}
