using System.Diagnostics;

namespace Fetchr.Core.Tests;

public sealed class ArchiveNameTests
{
    [Theory]
    [InlineData("movie.part1.rar", "rar", "movie", "movie.part1.rar", true)]
    [InlineData("movie.part02.rar", "rar", "movie", "movie.part01.rar", true)]
    [InlineData("backup.7z.003", "7z", "backup", "backup.7z.001", true)]
    [InlineData("disk.001", "split", "disk", "disk.001", true)]
    [InlineData("old.r00", "rar", "old", "old.rar", true)]
    [InlineData("site.z02", "zip", "site", "site.zip", true)]
    [InlineData("photos.zip", "zip", "photos", "photos.zip", false)]
    [InlineData("data.tar.gz", "gz", "data", "data.tar.gz", false)]
    [InlineData("data.tgz", "gz", "data", "data.tgz", false)]
    [InlineData("image.iso", "iso", "image", "image.iso", false)]
    public void Identifies_formats_and_first_parts(string name, string format, string baseName, string first, bool multi)
    {
        var a = Archives.Identify(name)!;
        Assert.Equal(format, a.Format);
        Assert.Equal(baseName, a.TargetName);
        Assert.Equal(first, a.FirstPart);
        Assert.Equal(multi, a.MultiPart);
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("report.pdf")]
    [InlineData("notes.txt")]
    public void Ignores_non_archives(string name) => Assert.Null(Archives.Identify(name));

    [Fact]
    public void Parts_of_one_set_share_a_key_and_other_sets_do_not()
    {
        var p1 = Archives.Identify("movie.part1.rar")!;
        Assert.True(Archives.SameSet("movie.part3.rar", p1));
        Assert.True(Archives.SameSet("MOVIE.PART2.RAR", p1));
        Assert.False(Archives.SameSet("other.part2.rar", p1));
        Assert.True(Archives.SameSet("old.r01", Archives.Identify("old.rar")!));
        Assert.True(Archives.SameSet("site.z01", Archives.Identify("site.zip")!));
    }

    [Fact]
    public void Classifies_seven_zip_output()
    {
        Assert.Equal(SevenZip.Outcome.MissingVolume, SevenZip.Classify(2, "ERRORS:\nMissing volume : set.part2.rar\nERROR: Data Error : big.bin"));
        Assert.Equal(SevenZip.Outcome.WrongPassword, SevenZip.Classify(2, "ERROR: Wrong password : a.txt"));
        Assert.Equal(SevenZip.Outcome.WrongPassword, SevenZip.Classify(2, "ERROR: x.rar : Cannot open encrypted archive. Wrong password?"));
        Assert.Equal(SevenZip.Outcome.Damaged, SevenZip.Classify(2, "ERROR: x.7z\nCannot open the file as archive"));
        Assert.Equal(SevenZip.Outcome.Ok, SevenZip.Classify(0, "Everything is Ok"));
    }
}

/// <summary>Real archives made by 7-Zip (and WinRAR where installed), unpacked through the whole pipeline.</summary>
public sealed class SevenZipExtractTests : IDisposable
{
    private static readonly SevenZip? Sz = SevenZip.Find();
    private static readonly string? RarExe = new[] { @"C:\Program Files\WinRAR\Rar.exe", @"C:\Program Files (x86)\WinRAR\Rar.exe" }.FirstOrDefault(File.Exists);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fetchr-tests", Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly Store _store;

    public SevenZipExtractTests()
    {
        _src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(_src, "sub"));
        File.WriteAllText(Path.Combine(_src, "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(_src, "sub", "inner.txt"), "inner");
        File.WriteAllBytes(Path.Combine(_src, "noise.bin"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(300_000));
        _store = new Store(Path.Combine(_dir, "fetchr.db"));
    }

    private string Out => Directory.CreateDirectory(Path.Combine(_dir, "dl")).FullName;

    private static void Run(string exe, string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private void SevenZipA(string archive, params string[] extra) =>
        Run(Sz!.ExePath, _src, ["a", "-bso0", "-bsp0", .. extra, Path.Combine(Out, archive), "*"]);

    private ArchiveService Service => new(_store, () => Sz);

    private void AssertUnpacked(string target)
    {
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "readme.txt")));
        Assert.Equal("inner", File.ReadAllText(Path.Combine(target, "sub", "inner.txt")));
    }

    [SkippableFact]
    public async Task Unpacks_7z_into_a_folder_named_after_it()
    {
        Skip.If(Sz is null, "7-Zip not installed");
        SevenZipA("bundle.7z");
        var r = await Service.ExtractSetAsync(Out, Archives.Identify("bundle.7z")!, null);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal(Path.Combine(Out, "bundle"), r.Target);
        AssertUnpacked(r.Target!);
    }

    [SkippableFact]
    public async Task Tar_gz_is_unpacked_through_the_inner_tar()
    {
        Skip.If(Sz is null, "7-Zip not installed");
        Run(Sz!.ExePath, _src, "a", "-bso0", "-ttar", Path.Combine(_dir, "data.tar"), "*");
        Run(Sz.ExePath, _dir, "a", "-bso0", "-tgzip", Path.Combine(Out, "data.tar.gz"), Path.Combine(_dir, "data.tar"));
        var r = await Service.ExtractSetAsync(Out, Archives.Identify("data.tar.gz")!, null);
        Assert.Equal(ExtractState.Done, r.State);
        AssertUnpacked(r.Target!);
        Assert.False(File.Exists(Path.Combine(r.Target!, "data.tar")), "the intermediate .tar is removed");
    }

    [SkippableFact]
    public async Task Split_7z_waits_for_missing_volumes_then_unpacks()
    {
        Skip.If(Sz is null, "7-Zip not installed");
        SevenZipA("split.7z", "-v100k");
        var held = Path.Combine(_dir, "held.002");
        File.Move(Path.Combine(Out, "split.7z.002"), held);

        var info = Archives.Identify("split.7z.003")!;
        var waiting = await Service.ExtractSetAsync(Out, info, null);
        Assert.Equal(ExtractState.WaitingParts, waiting.State);
        Assert.False(Directory.Exists(Path.Combine(Out, "split")), "nothing is left behind while waiting");

        File.Move(held, Path.Combine(Out, "split.7z.002"));
        var done = await Service.ExtractSetAsync(Out, info, null);
        Assert.Equal(ExtractState.Done, done.State);
        AssertUnpacked(done.Target!);
    }

    [SkippableFact]
    public async Task Password_is_asked_for_then_remembered_and_tried_automatically()
    {
        Skip.If(Sz is null, "7-Zip not installed");
        SevenZipA("secret.7z", "-pletmein", "-mhe=on");
        var info = Archives.Identify("secret.7z")!;

        Assert.Equal(ExtractState.NeedsPassword, (await Service.ExtractSetAsync(Out, info, null)).State);
        Assert.Equal(ExtractState.WrongPassword, (await Service.ExtractSetAsync(Out, info, "nope")).State);
        Assert.False(Directory.Exists(Path.Combine(Out, "secret")), "a wrong password leaves no half-made folder");

        _store.AddArchivePassword(Secrets.Protect("letmein"));
        var r = await Service.ExtractSetAsync(Out, info, null);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.NotNull(r.SavedPasswordId);
        AssertUnpacked(r.Target!);
    }

    [SkippableFact]
    public async Task Encrypted_zip_falls_back_from_the_built_in_reader_to_7zip()
    {
        Skip.If(Sz is null, "7-Zip not installed");
        SevenZipA("locked.zip", "-tzip", "-pzz");
        var r = await Service.ExtractSetAsync(Out, Archives.Identify("locked.zip")!, "zz");
        Assert.Equal(ExtractState.Done, r.State);
        AssertUnpacked(r.Target!);
    }

    [SkippableFact]
    public async Task Multi_part_rar_unpacks_once_every_part_is_there()
    {
        Skip.If(Sz is null || RarExe is null, "7-Zip or WinRAR not installed");
        Run(RarExe!, _src, "a", "-r", "-idq", "-m0", "-v120k", Path.Combine(Out, "movie.rar"), "*");
        Assert.True(File.Exists(Path.Combine(Out, "movie.part3.rar")));
        var held = Path.Combine(_dir, "held.rar");
        File.Move(Path.Combine(Out, "movie.part2.rar"), held);

        var info = Archives.Identify("movie.part3.rar")!;
        Assert.Equal(ExtractState.WaitingParts, (await Service.ExtractSetAsync(Out, info, null)).State);
        File.Move(held, Path.Combine(Out, "movie.part2.rar"));
        var r = await Service.ExtractSetAsync(Out, info, null);
        Assert.Equal(ExtractState.Done, r.State);
        Assert.Equal(Path.Combine(Out, "movie"), r.Target);
        AssertUnpacked(r.Target!);
    }

    [SkippableFact]
    public async Task Rar_with_password()
    {
        Skip.If(Sz is null || RarExe is null, "7-Zip or WinRAR not installed");
        Run(RarExe!, _src, "a", "-r", "-idq", "-psecret", Path.Combine(Out, "locked.rar"), "*");
        var info = Archives.Identify("locked.rar")!;
        Assert.Equal(ExtractState.NeedsPassword, (await Service.ExtractSetAsync(Out, info, null)).State);
        var r = await Service.ExtractSetAsync(Out, info, "secret");
        Assert.Equal(ExtractState.Done, r.State);
        AssertUnpacked(r.Target!);
    }

    [Fact]
    public async Task Without_7zip_a_rar_asks_for_it_instead_of_failing_silently()
    {
        File.WriteAllBytes(Path.Combine(Out, "x.rar"), [0x52, 0x61, 0x72, 0x21]);
        var r = await new ArchiveService(_store, () => null).ExtractSetAsync(Out, Archives.Identify("x.rar")!, null);
        Assert.Equal(ExtractState.NeedsSevenZip, r.State);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
