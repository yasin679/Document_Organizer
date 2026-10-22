using System.Text.Json;
using DocumentOrganizer.Domain;

namespace DocumentOrganizer.Infrastructure;

/// <summary>Schema and data access for jobs, items, users, settings and the audit log.</summary>
public sealed class AppDb
{
    public SqliteDb Db { get; }
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AppDb(SqliteDb db)
    {
        Db = db;
        Migrate();
    }

    private void Migrate()
    {
        Db.Execute(@"CREATE TABLE IF NOT EXISTS users(
            user_name TEXT PRIMARY KEY COLLATE NOCASE, display_name TEXT, role TEXT NOT NULL,
            password_hash TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1, created_at TEXT, last_login TEXT)");
        Db.Execute(@"CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT)");
        Db.Execute(@"CREATE TABLE IF NOT EXISTS audit(
            id INTEGER PRIMARY KEY AUTOINCREMENT, ts TEXT NOT NULL, user_name TEXT, role TEXT,
            event TEXT NOT NULL, job_id TEXT, details TEXT, ip TEXT)");
        Db.Execute("CREATE INDEX IF NOT EXISTS ix_audit_ts ON audit(ts)");
        Db.Execute(@"CREATE TABLE IF NOT EXISTS jobs(
            id TEXT PRIMARY KEY, name TEXT, source_json TEXT, dest_json TEXT, config_json TEXT,
            mapping_file TEXT, mapping_original_name TEXT, mapping_rows INTEGER, mapping_sha256 TEXT, mapping_info_json TEXT,
            status TEXT NOT NULL, message TEXT, created_by TEXT, created_at TEXT,
            scan_started_at TEXT, scan_ended_at TEXT, approved_by TEXT, approved_at TEXT,
            started_at TEXT, ended_at TEXT, progress_done INTEGER DEFAULT 0, progress_total INTEGER DEFAULT 0,
            counts_json TEXT, discovered INTEGER DEFAULT 0)");
        Db.Execute(@"CREATE TABLE IF NOT EXISTS items(
            id INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL, source_key TEXT, file_name TEXT,
            source_size INTEGER, source_etag TEXT, source_md5 TEXT, access_tier TEXT,
            record_id TEXT, employee_code TEXT, employee_name TEXT, doc_type TEXT, doc_name TEXT, description TEXT,
            uploaded_on TEXT, module_code TEXT, submodule_code TEXT, year TEXT,
            dest_key TEXT, status TEXT NOT NULL, reason TEXT, candidates_json TEXT,
            source_hash TEXT, dest_hash TEXT, integrity_method TEXT, processed_at TEXT, processed_by TEXT,
            attempts INTEGER DEFAULT 0, started_at TEXT, resolved_by TEXT, resolution_note TEXT,
            FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE)");
        Db.Execute("CREATE INDEX IF NOT EXISTS ix_items_job_status ON items(job_id, status)");
        Db.Execute("CREATE INDEX IF NOT EXISTS ix_items_job_dest ON items(job_id, dest_key)");
        // v2.1: several mapping files per job; each item remembers which mapping file its record came from
        AddColumnIfMissing("jobs", "mappings_json", "TEXT");
        AddColumnIfMissing("items", "mapping_file", "TEXT");
    }

    private void AddColumnIfMissing(string table, string column, string type)
    {
        var cols = Db.Query($"PRAGMA table_info({table})").Select(r => r.Str("name")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!cols.Contains(column)) Db.Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    // ---------- mapping files ----------
    /// <summary>The job's mapping files. Jobs created before v2.1 have one file stored in the mapping_* columns.</summary>
    public static List<MappingSource> Mappings(Row job)
    {
        if (job.Str("mappings_json") is string mj && mj.Length > 2)
            return JsonSerializer.Deserialize<List<MappingSource>>(mj, Json) ?? new();
        if (job.Str("mapping_file") is not string path) return new();
        var info = job.Str("mapping_info_json") is string ij ? JsonSerializer.Deserialize<JsonElement>(ij, Json) : default;
        var m = new MappingSource
        {
            Id = "m1", Path = path, OriginalName = job.Str("mapping_original_name") ?? Path.GetFileName(path),
            Rows = (int)job.Long("mapping_rows"), Sha256 = job.Str("mapping_sha256") ?? ""
        };
        if (info.ValueKind == JsonValueKind.Object)
        {
            if (info.TryGetProperty("headerRow", out var hr)) m.HeaderRow = hr.GetInt32();
            if (info.TryGetProperty("headers", out var h)) m.Headers = h.Deserialize<string[]>(Json) ?? Array.Empty<string>();
            if (info.TryGetProperty("preview", out var pv)) m.Preview = pv.Deserialize<List<string[]>>(Json) ?? new();
        }
        return new() { m };
    }

    /// <summary>Stores the mapping files and keeps the summary columns (first file, total rows) up to date.</summary>
    public void SaveMappings(string jobId, List<MappingSource> list)
    {
        var first = list.FirstOrDefault();
        UpdateJob(jobId,
            ("mappings_json", list.Count == 0 ? null : ToJson(list)),
            ("mapping_file", first?.Path),
            ("mapping_original_name", list.Count == 0 ? null : string.Join(", ", list.Select(m => m.OriginalName))),
            ("mapping_rows", list.Sum(m => (long)m.Rows)),
            ("mapping_sha256", first?.Sha256),
            ("mapping_info_json", first == null ? null : ToJson(new { headerRow = first.HeaderRow, headers = first.Headers, preview = first.Preview, dataRows = first.Rows })));
    }

    // ---------- settings ----------
    public string? GetSetting(string key) => Db.Scalar("SELECT value FROM settings WHERE key=@k", ("k", key)) as string;
    public void SetSetting(string key, string? value) => Db.Execute("INSERT OR REPLACE INTO settings(key,value) VALUES(@k,@v)", ("k", key), ("v", value));

    public AppSettings LoadSettings()
    {
        var s = new AppSettings();
        var raw = GetSetting("app");
        if (raw != null) { try { s = JsonSerializer.Deserialize<AppSettings>(raw, Json) ?? s; } catch { } }
        return s;
    }
    public void SaveSettings(AppSettings s) => SetSetting("app", JsonSerializer.Serialize(s, Json));

    // ---------- audit ----------
    public void Audit(string? user, string? role, string evt, string? jobId, string? details, string? ip = null) =>
        Db.Execute("INSERT INTO audit(ts,user_name,role,event,job_id,details,ip) VALUES(@ts,@u,@r,@e,@j,@d,@ip)",
            ("ts", DateTime.UtcNow.ToString("o")), ("u", user), ("r", role), ("e", evt), ("j", jobId), ("d", details), ("ip", ip));

    // ---------- jobs ----------
    public string NextJobId()
    {
        var prefix = "JOB-" + DateTime.Now.ToString("yyyyMMdd") + "-";
        var max = Db.Scalar("SELECT MAX(id) FROM jobs WHERE id LIKE @p", ("p", prefix + "%")) as string;
        int n = 1;
        if (max != null && int.TryParse(max[prefix.Length..], out var last)) n = last + 1;
        return prefix + n.ToString("0000");
    }

    public static Location Loc(string? json) => string.IsNullOrEmpty(json) ? new Location() : JsonSerializer.Deserialize<Location>(json, Json)!;
    public static JobConfig Cfg(string? json) => string.IsNullOrEmpty(json) ? new JobConfig() : JsonSerializer.Deserialize<JobConfig>(json, Json)!;
    public static string ToJson(object o) => JsonSerializer.Serialize(o, Json);

    public Row? GetJob(string id) => Db.QueryOne("SELECT * FROM jobs WHERE id=@id", ("id", id));

    public void UpdateJob(string id, params (string col, object? val)[] cols)
    {
        var set = string.Join(",", cols.Select(c => $"{c.col}=@{c.col}"));
        var args = cols.Select(c => (c.col, c.val)).Append(("id", (object?)id)).ToArray();
        Db.Execute($"UPDATE jobs SET {set} WHERE id=@id", args);
    }

    public Dictionary<string, long> CountByStatus(string jobId)
    {
        var d = ItemStatus.All.ToDictionary(s => s, _ => 0L);
        foreach (var r in Db.Query("SELECT status, COUNT(*) n FROM items WHERE job_id=@j GROUP BY status", ("j", jobId)))
            d[r.Str("status")!] = r.Long("n");
        return d;
    }

    public void RefreshCounts(string jobId)
    {
        var c = CountByStatus(jobId);
        var discovered = Convert.ToInt64(Db.Scalar("SELECT COUNT(*) FROM items WHERE job_id=@j AND source_key IS NOT NULL", ("j", jobId)) ?? 0L);
        UpdateJob(jobId, ("counts_json", ToJson(c)), ("discovered", discovered));
    }
}

public sealed class AppSettings
{
    public bool AllowOverwrite { get; set; } = false;
    public int MaxParallelCopies { get; set; } = 8;
    public int JobRetentionDays { get; set; } = 365;
    public int AuditRetentionDays { get; set; } = 2555;
    public bool LocalSourceEnabled { get; set; } = true;
    public bool AzureBlobEnabled { get; set; } = true;
    public bool S3Enabled { get; set; } = true;
}
