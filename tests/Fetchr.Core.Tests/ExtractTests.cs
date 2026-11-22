using System.IO.Compression;
using System.Text;

namespace Fetchr.Core.Tests;

public sealed class ExtractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fetchr-tests", Guid.NewGuid().ToString("N"));

    public ExtractTests() => Directory.CreateDirectory(_dir);

    private string MakeZip(string name, params (string Entry, string Text)[] files)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in files)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open(), Encoding.UTF8);
            w.Write(text);
        }
        return path;
    }

    [Fact]
    public void Extracts_into_a_folder_named_after_the_archive()
    {
        var zip = MakeZip("photos.zip", ("a.txt", "A"), ("sub/b.txt", "B"), ("empty/", ""));
        var target = Extractor.DefaultTarget(zip);
        var r = Extractor.ExtractZip(zip, target);

        Assert.Equal(Path.Combine(_dir, "photos"), target);
        Assert.Equal(2, r.Files);
        Assert.Equal("B", File.ReadAllText(Path.Combine(target, "sub", "b.txt")));
        Assert.True(Directory.Exists(Path.Combine(target, "empty")));
    }

    [Fact]
    public void Never_reuses_an_existing_folder()
    {
        var zip = MakeZip("photos.zip", ("a.txt", "A"));
        Directory.CreateDirectory(Path.Combine(_dir, "photos"));
        Assert.Equal(Path.Combine(_dir, "photos (1)"), Extractor.DefaultTarget(zip));
    }

    [Fact]
    public void Blocks_zip_slip_and_reserved_names_but_extracts_the_rest()
    {
        var zip = MakeZip("evil.zip",
            ("ok.txt", "fine"),
            ("../escaped.txt", "x"),
            ("sub/../../escaped2.txt", "x"),
            ("C:/Windows/evil.txt", "x"),
            ("/rooted.txt", "x"),
            ("CON.txt", "x"),
            ("dir/inner.txt", "fine"));
        var target = Path.Combine(_dir, "evil");
        var r = Extractor.ExtractZip(zip, target);

        Assert.Equal(2, r.Files);
        Assert.Equal(5, r.SkippedUnsafe);
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped2.txt")));
        Assert.Equal(["ok.txt", "inner.txt"], Directory.GetFiles(target, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order().Reverse());
    }

    [Fact]
    public void Refuses_archives_that_expand_absurdly()
    {
        var zip = MakeZip("bomb.zip", ("zeros.txt", new string('0', 5_000_000)));
        var ex = Assert.Throws<ExtractException>(() => Extractor.ExtractZip(zip, Path.Combine(_dir, "bomb")));
        Assert.Contains("zip bomb", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_dir, "bomb")));
    }

    [Fact]
    public void Damaged_archive_gives_a_clear_message()
    {
        var path = Path.Combine(_dir, "broken.zip");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5]);
        var ex = Assert.Throws<ExtractException>(() => Extractor.ExtractZip(path, Path.Combine(_dir, "broken")));
        Assert.Contains("damaged", ex.Message);
    }

    [Fact]
    public void Extracted_files_inherit_mark_of_the_web()
    {
        var zip = MakeZip("tool.zip", ("setup.exe", "MZ"));
        MarkOfTheWeb.Apply(zip, "https://example.com/tool.zip", null);
        var target = Path.Combine(_dir, "tool");
        Extractor.ExtractZip(zip, target);
        Assert.Contains("ZoneId=3", MarkOfTheWeb.Read(Path.Combine(target, "setup.exe")));
    }

    [Fact]
    public async Task Completed_zip_downloads_are_extracted_automatically()
    {
        var zipPath = MakeZip("bundle.zip", ("readme.txt", "hello"), ("bin/app.dll", new string('x', 10_000)));
        using var server = new TestServer(File.ReadAllBytes(zipPath));
        var store = new Store(Path.Combine(_dir, "fetchr.db"));
        await using var m = new DownloadManager(store) { FlushInterval = TimeSpan.FromMilliseconds(50) };

        var rec = m.Add(await m.ProbeAsync(server.Url("/bundle.zip")), Path.Combine(_dir, "dl"));
        await m.WaitAsync(rec.Id);
        for (var i = 0; i < 100 && store.GetDownload(rec.Id)!.Status != DownloadStatus.Extracted; i++) await Task.Delay(50);

        Assert.Equal(DownloadStatus.Extracted, store.GetDownload(rec.Id)!.Status);
        var target = store.LastExtractTarget(rec.Id)!;
        Assert.Equal("hello", File.ReadAllText(Path.Combine(target, "readme.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "dl", "bundle.zip")), "the archive itself is kept by default");
    }

    [Fact]
    public async Task Auto_extract_can_be_turned_off()
    {
        var zipPath = MakeZip("keep.zip", ("a.txt", "a"));
        using var server = new TestServer(File.ReadAllBytes(zipPath));
        var store = new Store(Path.Combine(_dir, "fetchr.db"));
        store.SetSetting("auto_extract", "false");
        await using var m = new DownloadManager(store);

        var rec = m.Add(await m.ProbeAsync(server.Url("/keep.zip")), Path.Combine(_dir, "dl"));
        await m.WaitAsync(rec.Id);
        await Task.Delay(300);
        Assert.Equal(DownloadStatus.Completed, store.GetDownload(rec.Id)!.Status);
        Assert.False(Directory.Exists(Path.Combine(_dir, "dl", "keep")));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
