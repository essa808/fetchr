using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Fetchr.Core;

/// <summary>
/// The SQLite database: schema on first open, and every query the app runs.
/// Access control is the Windows profile ACL on the file; there is one user.
/// </summary>
public sealed class Store
{
    private readonly string _cs;

    static Store()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Store(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = true }.ToString();
        Migrate();
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fetchr", "fetchr.db");

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        c.Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;");
        return c;
    }

    /// <summary>Scripts in order; user_version records how many have run. Fresh installs run them all.</summary>
    private static readonly string[] Migrations = ["schema.sql", "002.sql"];

    private void Migrate()
    {
        using var c = Open();
        var version = (int)c.ExecuteScalar<long>("PRAGMA user_version");
        if (version >= Migrations.Length) return;
        // Pragmas can't change inside a transaction: WAL is set once here (it persists in the file),
        // the per-connection ones in Open(). Foreign keys are off while tables are rebuilt.
        c.Execute("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = OFF;");
        for (var v = version; v < Migrations.Length; v++)
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Fetchr.Core." + Migrations[v])!;
            var sql = string.Join('\n', new StreamReader(s).ReadToEnd().Split('\n').Where(l => !l.StartsWith("PRAGMA")));
            using var tx = c.BeginTransaction();
            c.Execute(sql, transaction: tx);
            if (c.Query("PRAGMA foreign_key_check", transaction: tx).Any())
                throw new InvalidOperationException($"Database migration {Migrations[v]} broke a foreign key; nothing was changed.");
            c.Execute($"PRAGMA user_version = {v + 1}", transaction: tx);
            tx.Commit();
        }
        c.Execute("PRAGMA foreign_keys = ON;");
    }

    // ---- settings ----

    public string? GetSetting(string key)
    {
        using var c = Open();
        return c.ExecuteScalar<string?>("select value from settings where key = @key", new { key });
    }

    public bool GetBool(string key, bool fallback) => GetSetting(key) switch
    {
        "true" => true,
        "false" => false,
        _ => fallback,
    };

    public int GetInt(string key, int fallback) => int.TryParse(GetSetting(key), out var v) ? v : fallback;

    public void SetSetting(string key, string jsonValue)
    {
        using var c = Open();
        c.Execute("""
            insert into settings (key, value) values (@key, @jsonValue)
            on conflict(key) do update set value = excluded.value,
              updated_at = strftime('%Y-%m-%dT%H:%M:%SZ','now')
            """, new { key, jsonValue });
    }

    // ---- categories ----

    /// <summary>The save folder for a file name, by extension, with %DOWNLOADS% resolved.</summary>
    public (string? CategoryId, string Folder) ResolveCategory(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        using var c = Open();
        var rows = c.Query<(string Id, string Extensions, string SaveFolder)>(
            "select id, extensions, save_folder from categories order by sort_order");
        foreach (var r in rows)
        {
            var exts = r.Extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (exts.Length == 0 || exts.Contains(ext)) return (r.Id, KnownFolders.Expand(r.SaveFolder));
        }
        return (null, KnownFolders.Downloads);
    }

    // ---- downloads ----

    private const string DownloadColumns = """
        id, url, final_url, referer, file_name, save_path, category_id, total_size, downloaded_bytes,
        status, resumable, etag, last_modified, max_connections, last_error, request_headers_dpapi, created_at, completed_at
        """;

    public void InsertDownload(DownloadRecord d, IEnumerable<SegmentRecord> segments)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        c.Execute($"""
            insert into downloads ({DownloadColumns}) values
            (@Id, @Url, @FinalUrl, @Referer, @FileName, @SavePath, @CategoryId, @TotalSize, @DownloadedBytes,
             @Status, @Resumable, @Etag, @LastModified, @MaxConnections, @LastError, @RequestHeadersDpapi, @CreatedAt, @CompletedAt)
            """, d, tx);
        InsertSegments(c, tx, segments);
        tx.Commit();
    }

    private static void InsertSegments(SqliteConnection c, SqliteTransaction tx, IEnumerable<SegmentRecord> segments) =>
        c.Execute("""
            insert into segments (id, download_id, start_byte, end_byte, current_byte, status)
            values (@Id, @DownloadId, @StartByte, @EndByte, @CurrentByte, @Status)
            """, segments, tx);

    /// <summary>True when another live download already targets this exact file.</summary>
    public bool TargetTaken(string folder, string fileName)
    {
        using var c = Open();
        return c.ExecuteScalar<long>("""
            select count(*) from downloads
            where lower(save_path) = lower(@folder) and lower(file_name) = lower(@fileName) and is_removed = 0
            """, new { folder, fileName }) > 0;
    }

    public DownloadRecord? GetDownload(string id)
    {
        using var c = Open();
        return c.QuerySingleOrDefault<DownloadRecord>(
            $"select {DownloadColumns} from downloads where id = @id", new { id });
    }

    public List<DownloadRecord> ListDownloads()
    {
        using var c = Open();
        return c.Query<DownloadRecord>(
            $"select {DownloadColumns} from downloads where is_removed = 0 order by created_at desc").ToList();
    }

    public List<SegmentRecord> GetSegments(string downloadId)
    {
        using var c = Open();
        return c.Query<SegmentRecord>("""
            select id, download_id, start_byte, end_byte, current_byte, status
            from segments where download_id = @downloadId order by start_byte
            """, new { downloadId }).ToList();
    }

    /// <summary>Replace a download's segments (used when a server turns out not to honour Range).</summary>
    public void ReplaceSegments(string downloadId, IEnumerable<SegmentRecord> segments)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        c.Execute("delete from segments where download_id = @downloadId", new { downloadId }, tx);
        InsertSegments(c, tx, segments);
        tx.Commit();
    }

    /// <summary>
    /// Save progress. Callers flush the file data to disk first, so the stored offsets
    /// never run ahead of bytes that are really on disk.
    /// </summary>
    public void SaveProgress(string downloadId, long downloaded, IEnumerable<(string Id, long Current, long End, string Status)> segments)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        c.Execute("update segments set current_byte = @Current, end_byte = @End, status = @Status where id = @Id",
            segments.Select(s => new { s.Id, s.Current, s.End, s.Status }), tx);
        c.Execute("update downloads set downloaded_bytes = @downloaded where id = @downloadId",
            new { downloaded, downloadId }, tx);
        tx.Commit();
    }

    public void UpdateDownload(DownloadRecord d)
    {
        using var c = Open();
        c.Execute("""
            update downloads set final_url = @FinalUrl, file_name = @FileName, save_path = @SavePath,
              total_size = @TotalSize, downloaded_bytes = @DownloadedBytes, status = @Status,
              resumable = @Resumable, etag = @Etag, last_modified = @LastModified,
              last_error = @LastError, request_headers_dpapi = @RequestHeadersDpapi, completed_at = @CompletedAt
            where id = @Id
            """, d);
    }

    public void SetStatus(string id, string status, string? error = null)
    {
        using var c = Open();
        c.Execute("update downloads set status = @status, last_error = @error where id = @id", new { id, status, error });
    }

    public void RemoveDownload(string id)
    {
        using var c = Open();
        c.Execute("update downloads set is_removed = 1 where id = @id", new { id });
    }

    /// <summary>On startup nothing is running yet: anything left "downloading" was interrupted.</summary>
    public int MarkInterruptedAsPaused()
    {
        using var c = Open();
        c.Execute("update extract_jobs set status = 'failed', error = 'Interrupted' where status = 'running'");
        c.Execute("update downloads set status = 'completed' where status = 'extracting'");
        return c.Execute("update downloads set status = 'paused' where status = 'downloading'");
    }

    // ---- extract jobs ----

    public string StartExtractJob(string downloadId, string setKey, string format, string target)
    {
        var id = Guid.NewGuid().ToString();
        using var c = Open();
        c.Execute("""
            insert into extract_jobs (id, download_id, archive_set_key, archive_format, target_path, status)
            values (@id, @downloadId, @setKey, @format, @target, 'running')
            """, new { id, downloadId, setKey, format, target });
        return id;
    }

    public void FinishExtractJob(string jobId, bool ok, string? error)
    {
        using var c = Open();
        c.Execute("""
            update extract_jobs set status = @status, error = @error, progress_pct = case when @ok then 100 else progress_pct end,
              finished_at = strftime('%Y-%m-%dT%H:%M:%SZ','now')
            where id = @jobId
            """, new { jobId, ok, error, status = ok ? "done" : "failed" });
    }

    public string? LastExtractTarget(string downloadId)
    {
        using var c = Open();
        return c.ExecuteScalar<string?>("""
            select target_path from extract_jobs where download_id = @downloadId and status = 'done'
            order by created_at desc limit 1
            """, new { downloadId });
    }

    /// <summary>Record that a whole archive set was unpacked by one job, so every part shows the folder.</summary>
    public void RecordExtractTarget(IEnumerable<string> downloadIds, string setKey, string format, string target)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var downloadId in downloadIds)
            c.Execute("""
                insert into extract_jobs (id, download_id, archive_set_key, archive_format, target_path, status, progress_pct, finished_at)
                values (@id, @downloadId, @setKey, @format, @target, 'done', 100, strftime('%Y-%m-%dT%H:%M:%SZ','now'))
                """, new { id = Guid.NewGuid().ToString(), downloadId, setKey = setKey + "#" + downloadId, format, target }, tx);
        tx.Commit();
    }

    // ---- categories ----

    public List<(string Id, string Name)> ListCategories()
    {
        using var c = Open();
        return c.Query<(string, string)>("select id, name from categories order by sort_order, name").ToList();
    }

    // ---- queue ----

    /// <summary>How many downloads may run at once (the main queue's limit).</summary>
    public int MaxParallel()
    {
        using var c = Open();
        return (int)(c.ExecuteScalar<long?>("select max_parallel from queues order by created_at limit 1") ?? 3);
    }

    public void SetMaxParallel(int n)
    {
        using var c = Open();
        c.Execute("update queues set max_parallel = @n", new { n = Math.Clamp(n, 1, 16) });
    }

    /// <summary>Queued downloads, oldest first: the order they get a free slot.</summary>
    public List<string> QueuedIds()
    {
        using var c = Open();
        return c.Query<string>("select id from downloads where status = 'queued' and is_removed = 0 order by created_at, rowid").ToList();
    }

    // ---- archive passwords (DPAPI-encrypted) ----

    public List<(string Id, byte[] Secret)> ArchivePasswords()
    {
        using var c = Open();
        return c.Query<(string, byte[])>("select id, password_dpapi from archive_passwords order by use_count desc, created_at desc").ToList();
    }

    public void AddArchivePassword(byte[] secret)
    {
        using var c = Open();
        c.Execute("insert into archive_passwords (id, password_dpapi) values (@id, @secret)", new { id = Guid.NewGuid().ToString(), secret });
    }

    public void MarkPasswordUsed(string id)
    {
        using var c = Open();
        c.Execute("""
            update archive_passwords set use_count = use_count + 1, last_used_at = strftime('%Y-%m-%dT%H:%M:%SZ','now'),
              updated_at = strftime('%Y-%m-%dT%H:%M:%SZ','now') where id = @id
            """, new { id });
    }
}
