using System.Text;
using DocumentOrganizer.Domain;
using DocumentOrganizer.Infrastructure;

namespace DocumentOrganizer.Engine;

/// <summary>Validation (pre-approval) and reconciliation (post-processing) reports as Excel or CSV.</summary>
public sealed class Reports(AppDb app)
{
    private static readonly string[] ValidationHeaders =
    {
        "Item ID", "Status", "Reason", "Mapping Record ID", "Mapping File", "EmployeeCode", "Employee Name", "Document Type", "Document Name",
        "Description", "UploadedOn", "Year", "Source File", "Source Path", "Proposed Destination", "Size (bytes)", "Access Tier",
        "Excluded From Status", "Excluded By", "Excluded At (UTC)", "Exclusion Note"
    };

    private static readonly string[] ReconHeaders =
    {
        "Job ID", "Mapping Record ID", "Mapping File", "EmployeeCode", "Employee Name", "Document Type", "Document Name", "Description",
        "UploadedOn", "Source File", "Source Path", "Destination Path", "Year", "Status", "Error/Exception Reason", "Source Hash",
        "Destination Hash", "Integrity Method", "Source ETag", "Processed Timestamp (UTC)", "Processed By",
        "Excluded From Status", "Excluded By", "Excluded At (UTC)", "Exclusion Note"
    };

    public async Task WriteAsync(string jobId, string kind, string format, Stream output)
    {
        var job = app.GetJob(jobId) ?? throw new InvalidOperationException("Job not found.");
        var src = AppDb.Loc(job.Str("source_json"));
        var dst = AppDb.Loc(job.Str("dest_json"));
        bool recon = kind == "reconciliation";
        var headers = recon ? ReconHeaders : ValidationHeaders;
        string Join(Location l, string? key) => key == null ? "" : l.Type == "Local"
            ? Path.Combine(l.Path ?? "", key.Replace('/', Path.DirectorySeparatorChar))
            : $"{l.Container}/{(l.Prefix ?? "").Trim('/')}{((l.Prefix ?? "").Trim('/').Length > 0 ? "/" : "")}{key}";

        string?[] Map(Row r) => recon
            ? new[]
            {
                jobId, r.Str("record_id"), r.Str("mapping_file"), r.Str("employee_code"), r.Str("employee_name"),
                r.Str("doc_type"), r.Str("doc_name"), r.Str("description"), r.Str("uploaded_on"), r.Str("file_name"), Join(src, r.Str("source_key")),
                r.Str("status") == ItemStatus.Processed ? Join(dst, r.Str("dest_key")) : "", r.Str("year"), r.Str("status"), r.Str("reason"),
                r.Str("source_hash"), r.Str("dest_hash"), r.Str("integrity_method"), r.Str("source_etag"), r.Str("processed_at"), r.Str("processed_by"),
                r.Str("excluded_from"), r.Str("excluded_by"), r.Str("excluded_at"), r.Str("exclusion_note")
            }
            : new[]
            {
                r.Str("id"), r.Str("status"), r.Str("reason"), r.Str("record_id"), r.Str("mapping_file"), r.Str("employee_code"),
                r.Str("employee_name"), r.Str("doc_type"), r.Str("doc_name"), r.Str("description"), r.Str("uploaded_on"), r.Str("year"),
                r.Str("file_name"), Join(src, r.Str("source_key")), r.Str("dest_key") != null ? Join(dst, r.Str("dest_key")) : "",
                r.Str("source_size"), r.Str("access_tier"),
                r.Str("excluded_from"), r.Str("excluded_by"), r.Str("excluded_at"), r.Str("exclusion_note")
            };

        // Collect rows first (the DB lock must not be held while writing to the network stream).
        var rows = new List<string?[]>();
        app.Db.QueryEach("SELECT * FROM items WHERE job_id=@j ORDER BY CASE status WHEN 'READY' THEN 9 WHEN 'PROCESSED' THEN 9 WHEN 'EXCLUDED' THEN 5 ELSE 1 END, status, id", r => rows.Add(Map(r)), ("j", jobId));
        var counts = app.CountByStatus(jobId);

        // Build the report in a temporary file, then stream it (keeps memory flat for very large jobs).
        var tmp = Path.Combine(Path.GetTempPath(), "docorg-" + Guid.NewGuid().ToString("N") + "." + format);
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            {
                if (format == "csv")
                {
                    using var w = new StreamWriter(fs, new UTF8Encoding(true), 1 << 16, leaveOpen: true);
                    Csv.WriteRow(w, headers);
                    foreach (var r in rows) Csv.WriteRow(w, r);
                }
                else WriteXlsx(fs, job, jobId, src, dst, recon, headers, rows, counts);
                fs.Position = 0;
                await fs.CopyToAsync(output);
            }
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private static void WriteXlsx(Stream fs, Row job, string jobId, Location src, Location dst, bool recon, string[] headers, List<string?[]> rows, Dictionary<string, long> counts)
    {
        using (var x = new XlsxWriter(fs))
        {
            x.BeginSheet("Summary", new[] { "Field", "Value" }, new double[] { 34, 70 });
            x.WriteRow(new[] { "Report", recon ? "Reconciliation report" : "Validation report (before approval)" });
            x.WriteRow(new[] { "Job ID", jobId });
            x.WriteRow(new[] { "Job label", job.Str("name") });
            x.WriteRow(new[] { "Source", src.Describe() });
            x.WriteRow(new[] { "Destination", dst.Describe() });
            var maps = AppDb.Mappings(job);
            for (int i = 0; i < maps.Count; i++)
                x.WriteRow(new[] { maps.Count == 1 ? "Mapping file" : $"Mapping file {i + 1} of {maps.Count}", $"{maps[i].OriginalName} · {maps[i].Rows:N0} rows (SHA-256 {maps[i].Sha256})" });
            var cfg = AppDb.Cfg(job.Str("config_json"));
            x.WriteRow(new[] { "Year rule", cfg.YearRule + (cfg.YearRule is "FileNamePattern" or "PathPattern" ? $" ({cfg.YearPattern})" : "") });
            x.WriteRow(new[] { "Folder template", cfg.Template });
            x.WriteRow(new[] { "File matching several records", cfg.AmbiguousRule == "KeepAll" ? "Keep all: one copy per record" : "Flag as AMBIGUOUS" });
            x.WriteRow(new[] { "Duplicate rule", cfg.DuplicateRule });
            x.WriteRow(new[] { "Integrity check", cfg.Integrity });
            x.WriteRow(new[] { "Approved by", job.Str("approved_by") is string ab ? $"{ab} at {job.Str("approved_at")}" : "Not yet approved" });
            x.WriteRow(new[] { "Files discovered in source", job.Str("discovered") });
            foreach (var s in ItemStatus.All) x.WriteRow(new[] { "Items " + s, counts[s].ToString() });
            x.WriteRow(new[] { "Generated (UTC)", DateTime.UtcNow.ToString("u") });
            x.BeginSheet(recon ? "Reconciliation" : "Validation", headers);
            foreach (var r in rows) x.WriteRow(r);
        }
    }
}
