using DocumentOrganizer.Domain;
using DocumentOrganizer.Infrastructure;

namespace DocumentOrganizer.Engine;

/// <summary>
/// Bulk exclusion of exceptions. An excluded item gets status EXCLUDED, keeps its original status, reason and data,
/// and records who excluded it, when and why. It is never copied, is not counted as an exception, and stays in both
/// reports. Exclusions are remembered per job and applied again after a new scan to the same file or record,
/// as long as it ends up with the same status. Restoring puts the original status back.
/// </summary>
public sealed class Exclusions(AppDb app)
{
    /// <summary>Reason groups: SQL LIKE pattern on the reason text, and the label shown to people. First match wins.</summary>
    private static readonly (string Like, string Label)[] Groups =
    {
        ("Blank FileName%", "Blank file name"),
        ("Blank EmployeeCode%", "Blank employee code"),
        ("Blank Type%", "Blank document type"),
        ("Source inaccessible%archive%", "File in an archive tier"),
        ("Empty (0-byte)%", "Empty file"),
        ("Year could not be determined%", "Year not found"),
        ("Year % is out of range%", "Year out of range"),
        ("No mapping record matches%", "No mapping record"),
        ("% mapping records match this file%", "Several records match"),
        ("Duplicate physical file%", "Same file name twice in the source"),
        ("Mapping exists but no source file%(repeats record%", "Repeat of a missing record"),
        ("Mapping exists but no source file%", "File not in the source"),
        ("Duplicate mapping record%", "Repeated mapping record"),
        ("Destination path collision%", "Same destination path"),
        ("Record % is already matched%", "Record already matched"),
        ("Identical file already at destination%", "Already at destination"),
        ("Destination already exists%", "Already at destination"),
        ("Integrity check failed%", "Integrity check failed"),
        ("Source file changed after the scan%", "Source changed after scan"),
        ("Source file no longer exists%", "Source file gone"),
        ("%not found (404)%", "Not found (404)"),
    };

    /// <summary>SQL expression giving the reason group of an item.</summary>
    public static readonly string GroupSql =
        "CASE " + string.Join(" ", Groups.Select(g => $"WHEN reason LIKE '{g.Like.Replace("'", "''")}' THEN '{g.Label.Replace("'", "''")}'")) +
        " WHEN reason IS NULL OR reason='' THEN '(no reason)' ELSE 'Other: ' || substr(reason, 1, 70) END";

    /// <summary>
    /// Identifies an item across scans: the source file plus its record (a file kept for several records under the
    /// "keep all" rule has one item per record), or for a record without a file, its mapping file and record ID.
    /// </summary>
    public static string Key(string? sourceKey, string? mappingFile, string? recordId) =>
        sourceKey != null ? "f:" + sourceKey + "|" + (mappingFile ?? "") + "|" + (recordId ?? "") : "r:" + (mappingFile ?? "") + "|" + (recordId ?? "");

    public sealed record Filter(string? Status, string? Q, string? Group);

    /// <summary>Builds the WHERE clause for the item list and for bulk actions.</summary>
    public static (string Where, List<(string, object?)> Args) Where(string jobId, string? status, string? q, string? group)
    {
        var where = "job_id=@j";
        var args = new List<(string, object?)> { ("j", jobId) };
        if (!string.IsNullOrEmpty(status) && status != "ALL")
        {
            if (status == "EXCEPTIONS") where += " AND status IN (" + string.Join(",", ItemStatus.Excludable.Select(s => $"'{s}'")) + ")";
            else { where += " AND status=@s"; args.Add(("s", status)); }
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            where += " AND (source_key LIKE @q OR file_name LIKE @q OR record_id LIKE @q OR employee_code LIKE @q OR dest_key LIKE @q OR reason LIKE @q)";
            args.Add(("q", "%" + q.Trim() + "%"));
        }
        if (!string.IsNullOrWhiteSpace(group)) { where += $" AND ({GroupSql})=@g"; args.Add(("g", group)); }
        return (where, args);
    }

    /// <summary>Counts of exceptions (or excluded items) per status and reason group.</summary>
    public List<(string Status, string Group, long Count)> Reasons(string jobId, bool excluded)
    {
        var statusCol = excluded ? "excluded_from" : "status";
        var cond = excluded ? "status='EXCLUDED'" : "status IN (" + string.Join(",", ItemStatus.Excludable.Select(s => $"'{s}'")) + ")";
        return app.Db.Query($"SELECT {statusCol} st, {GroupSql} grp, COUNT(*) n FROM items WHERE job_id=@j AND {cond} GROUP BY 1, 2 ORDER BY 3 DESC", ("j", jobId))
            .Select(r => (r.Str("st") ?? "", r.Str("grp") ?? "", r.Long("n"))).ToList();
    }

    public static string? CannotChange(Row job, bool running)
    {
        if (running) return "The job is running. Pause it or wait until it finishes.";
        var st = job.Str("status");
        return st is JobStatus.AwaitingApproval or JobStatus.Paused or JobStatus.Completed or JobStatus.CompletedWithExceptions
            ? null : "Exceptions can be excluded after a scan, while the job is awaiting approval, paused or completed.";
    }

    /// <summary>Excludes the chosen exceptions. Items that are not exceptions (ready, processed, already excluded) are left alone.</summary>
    public (int Count, Dictionary<string, int> ByStatus) Exclude(string jobId, IReadOnlyCollection<long>? ids, Filter? filter, string note, string user)
    {
        var rows = Select(jobId, ids, filter, ItemStatus.Excludable);
        var now = DateTime.UtcNow.ToString("o");
        app.Db.InTransaction(() =>
        {
            foreach (var r in rows)
            {
                app.Db.Execute("UPDATE items SET excluded_from=status, status='EXCLUDED', excluded_by=@u, excluded_at=@t, exclusion_note=@n WHERE id=@id",
                    ("u", user), ("t", now), ("n", note), ("id", r.Id));
                app.Db.Execute("INSERT OR REPLACE INTO exclusions(job_id,item_key,from_status,note,by_user,at) VALUES(@j,@k,@s,@n,@u,@t)",
                    ("j", jobId), ("k", r.Key), ("s", r.Status), ("n", note), ("u", user), ("t", now));
            }
        });
        AfterChange(jobId);
        return (rows.Count, rows.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>Puts excluded items back to the status they had.</summary>
    public (int Count, Dictionary<string, int> ByStatus) Restore(string jobId, IReadOnlyCollection<long>? ids, Filter? filter)
    {
        var rows = Select(jobId, ids, filter, new[] { ItemStatus.Excluded });
        app.Db.InTransaction(() =>
        {
            foreach (var r in rows)
            {
                app.Db.Execute("UPDATE items SET status=COALESCE(excluded_from, status), excluded_from=NULL, excluded_by=NULL, excluded_at=NULL, exclusion_note=NULL WHERE id=@id", ("id", r.Id));
                app.Db.Execute("DELETE FROM exclusions WHERE job_id=@j AND item_key=@k", ("j", jobId), ("k", r.Key));
            }
        });
        AfterChange(jobId);
        return (rows.Count, rows.GroupBy(r => r.From ?? "").ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>After a scan: sets aside again the items that were excluded before, if they still have the same status.</summary>
    public static void ApplyRemembered(AppDb app, string jobId, List<ItemRow> items)
    {
        var saved = app.Db.Query("SELECT * FROM exclusions WHERE job_id=@j", ("j", jobId))
            .ToDictionary(r => r.Str("item_key")!, r => r, StringComparer.Ordinal);
        if (saved.Count == 0) return;
        foreach (var it in items)
            if (saved.TryGetValue(Key(it.SourceKey, it.MappingFile, it.RecordId), out var x) && x.Str("from_status") == it.Status)
            {
                it.ExcludedFrom = it.Status; it.Status = ItemStatus.Excluded;
                it.ExcludedBy = x.Str("by_user"); it.ExcludedAt = x.Str("at"); it.ExclusionNote = x.Str("note");
            }
    }

    private sealed record Sel(long Id, string Status, string? From, string Key);

    /// <summary>The items an exclude (or restore) would change right now: their IDs and count per status. Used for change requests.</summary>
    public (long[] Ids, Dictionary<string, int> ByStatus) Preview(string jobId, IReadOnlyCollection<long>? ids, Filter? filter, bool restore)
    {
        var rows = Select(jobId, ids, filter, restore ? new[] { ItemStatus.Excluded } : ItemStatus.Excludable);
        return (rows.Select(r => r.Id).ToArray(), rows.GroupBy(r => restore ? r.From ?? "" : r.Status).ToDictionary(g => g.Key, g => g.Count()));
    }

    private List<Sel> Select(string jobId, IReadOnlyCollection<long>? ids, Filter? filter, string[] allowed)
    {
        var inList = string.Join(",", allowed.Select(s => $"'{s}'"));
        List<Row> rows;
        if (ids is { Count: > 0 })
        {
            rows = new();
            foreach (var chunk in ids.Distinct().Chunk(500))
                rows.AddRange(app.Db.Query($"SELECT id, status, excluded_from, source_key, mapping_file, record_id FROM items WHERE job_id=@j AND status IN ({inList}) AND id IN ({string.Join(",", chunk)})", ("j", jobId)));
        }
        else if (filter != null)
        {
            var (where, args) = Where(jobId, filter.Status, filter.Q, filter.Group);
            rows = app.Db.Query($"SELECT id, status, excluded_from, source_key, mapping_file, record_id FROM items WHERE {where} AND status IN ({inList})", args.ToArray());
        }
        else throw new ArgumentException("Choose items or a filter.");
        return rows.Select(r => new Sel(r.Long("id"), r.Str("status")!, r.Str("excluded_from"), Key(r.Str("source_key"), r.Str("mapping_file"), r.Str("record_id")))).ToList();
    }

    private void AfterChange(string jobId)
    {
        app.RefreshCounts(jobId);
        // A completed job is "with exceptions" only while some exception is left
        var job = app.GetJob(jobId);
        if (job?.Str("status") is JobStatus.Completed or JobStatus.CompletedWithExceptions)
            app.UpdateJob(jobId, ("status", HasExceptions(app.CountByStatus(jobId)) ? JobStatus.CompletedWithExceptions : JobStatus.Completed));
    }

    public static bool HasExceptions(Dictionary<string, long> c) => c.Any(kv => kv.Key != ItemStatus.Processed && kv.Key != ItemStatus.Excluded && kv.Value > 0);
}
