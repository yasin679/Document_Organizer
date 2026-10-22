using System.Collections.Concurrent;
using System.Text.Json;
using DocumentOrganizer.Domain;
using DocumentOrganizer.Infrastructure;
using DocumentOrganizer.Storage;

namespace DocumentOrganizer.Engine;

/// <summary>Runs scans and copy jobs in the background and keeps their state in the database.</summary>
public sealed class JobService(AppDb app, Scanner scanner, Processor processor, StorageFactory storage, ILogger<JobService> log)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<string, Scanner.Progress> _scanProgress = new();

    public bool IsRunning(string jobId) => _running.ContainsKey(jobId);
    public Scanner.Progress? ScanProgress(string jobId) => _scanProgress.TryGetValue(jobId, out var p) ? p : null;
    private readonly ConcurrentDictionary<string, CopyProgress> _copyProgress = new();
    public CopyProgress? CopyProgressOf(string jobId) => _copyProgress.TryGetValue(jobId, out var p) ? p : null;

    /// <summary>After a restart, interrupted work is marked so the user can resume it.</summary>
    public void RecoverAfterRestart()
    {
        foreach (var j in app.Db.Query("SELECT id, status FROM jobs WHERE status IN (@a,@b)", ("a", JobStatus.Scanning), ("b", JobStatus.Processing)))
        {
            var id = j.Str("id")!;
            if (j.Str("status") == JobStatus.Scanning)
                app.UpdateJob(id, ("status", JobStatus.Draft), ("message", "Scan was interrupted by an application restart. Run the scan again."));
            else
                app.UpdateJob(id, ("status", JobStatus.Paused), ("message", "Processing was interrupted by an application restart. Resume to continue; completed files are not copied again."));
            app.Audit("System", null, "Job interrupted", id, "Application restarted while the job was running.");
        }
    }

    public void StartScan(string jobId, string user, string role)
    {
        if (!_running.TryAdd(jobId, new CancellationTokenSource())) throw new InvalidOperationException("This job is already running.");
        var cts = _running[jobId];
        var progress = new Scanner.Progress();
        _scanProgress[jobId] = progress;
        app.UpdateJob(jobId, ("status", JobStatus.Scanning), ("message", null), ("scan_started_at", DateTime.UtcNow.ToString("o")), ("scan_ended_at", null));
        app.Audit(user, role, "Scan started", jobId, null);
        _ = Task.Run(async () =>
        {
            var started = DateTime.UtcNow;
            try
            {
                await scanner.ScanAsync(jobId, progress, cts.Token);
                var c = app.CountByStatus(jobId);
                var discovered = app.GetJob(jobId)!.Long("discovered");
                app.UpdateJob(jobId, ("status", JobStatus.AwaitingApproval), ("scan_ended_at", DateTime.UtcNow.ToString("o")),
                    ("message", null));
                app.Audit("System", null, "Validation completed", jobId,
                    $"{discovered:N0} files discovered in {(DateTime.UtcNow - started).TotalSeconds:N0}s · {c[ItemStatus.Ready]:N0} ready · " +
                    $"{c[ItemStatus.Unmapped]:N0} unmapped · {c[ItemStatus.Missing]:N0} missing · {c[ItemStatus.Ambiguous]:N0} ambiguous · {c[ItemStatus.Duplicate]:N0} duplicate · {c[ItemStatus.Invalid]:N0} invalid");
            }
            catch (OperationCanceledException)
            {
                app.UpdateJob(jobId, ("status", JobStatus.Draft), ("message", "Scan cancelled."));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Scan failed for {Job}", jobId);
                app.UpdateJob(jobId, ("status", JobStatus.Draft), ("message", "Scan failed: " + ex.Message));
                app.Audit("System", null, "Scan failed", jobId, ex.Message);
            }
            finally { _running.TryRemove(jobId, out _); _scanProgress.TryRemove(jobId, out _); }
        });
    }

    public void StartProcessing(string jobId, string user, string role, string eventName)
    {
        if (!_running.TryAdd(jobId, new CancellationTokenSource())) throw new InvalidOperationException("This job is already running.");
        var cts = _running[jobId];
        var now = DateTime.UtcNow.ToString("o");
        var job = app.GetJob(jobId)!;
        app.UpdateJob(jobId, ("status", JobStatus.Processing), ("message", null), ("started_at", job.Str("started_at") ?? now), ("ended_at", null));
        app.Audit(user, role, eventName, jobId, null);
        _ = Task.Run(async () =>
        {
            try
            {
                var cp = _copyProgress[jobId] = new CopyProgress();
                await processor.RunAsync(jobId, user, cts.Token, cp);
                if (cts.IsCancellationRequested)
                {
                    app.UpdateJob(jobId, ("status", JobStatus.Paused), ("message", "Paused. Resume to continue with the remaining files."));
                    app.Audit(user, role, "Processing paused", jobId, null);
                    return;
                }
                var c = app.CountByStatus(jobId);
                bool exceptions = Exclusions.HasExceptions(c);
                app.UpdateJob(jobId, ("status", exceptions ? JobStatus.CompletedWithExceptions : JobStatus.Completed), ("ended_at", DateTime.UtcNow.ToString("o")));
                app.Audit("System", null, "Processing completed", jobId,
                    $"{c[ItemStatus.Processed]:N0} processed · {c[ItemStatus.Failed]:N0} failed · {c[ItemStatus.Duplicate]:N0} duplicate");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Processing failed for {Job}", jobId);
                app.UpdateJob(jobId, ("status", JobStatus.Paused), ("message", "Processing stopped: " + ex.Message + " Fix the cause and resume."));
                app.Audit("System", null, "Processing stopped", jobId, ex.Message);
            }
            finally { _running.TryRemove(jobId, out _); _copyProgress.TryRemove(jobId, out _); app.RefreshCounts(jobId); }
        });
    }

    public bool Cancel(string jobId)
    {
        if (_running.TryGetValue(jobId, out var cts)) { cts.Cancel(); return true; }
        return false;
    }

    /// <summary>Assigns an AMBIGUOUS file to one of its candidate records.</summary>
    public (bool ok, string message) Resolve(string jobId, long itemId, string recordId, string? note, string user, string role, string? mappingFile = null)
    {
        var item = app.Db.QueryOne("SELECT * FROM items WHERE id=@id AND job_id=@j", ("id", itemId), ("j", jobId));
        if (item == null) return (false, "Item not found.");
        if (item.Str("status") != ItemStatus.Ambiguous) return (false, "Only ambiguous items can be resolved.");
        var cands = JsonSerializer.Deserialize<List<Candidate>>(item.Str("candidates_json") ?? "[]", AppDb.Json) ?? new();
        var rec = cands.FirstOrDefault(c => c.RecordId == recordId && (mappingFile == null || c.MappingFile == mappingFile)) ?? cands.FirstOrDefault(c => c.RecordId == recordId);
        if (rec == null) return (false, "That record is not one of the candidates for this file.");
        var cfg = AppDb.Cfg(app.GetJob(jobId)!.Str("config_json"));
        var f = new SourceFile { Key = item.Str("source_key")!, Size = item.Long("source_size"), ETag = item.Str("source_etag"), ContentMd5 = item.Str("source_md5"), AccessTier = item.Str("access_tier") };
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ev = Scanner.Evaluate(f, rec, cfg, taken);
        if (ev.DestKey != null)
        {
            var clash = app.Db.QueryOne("SELECT source_key FROM items WHERE job_id=@j AND dest_key=@d COLLATE NOCASE AND status IN ('READY','PROCESSED') AND id<>@id",
                ("j", jobId), ("d", ev.DestKey), ("id", itemId));
            if (clash != null) { ev.Status = ItemStatus.Duplicate; ev.Reason = $"Destination path collision: {clash.Str("source_key")} already maps to {ev.DestKey}."; }
        }
        var claimedBy = app.Db.QueryOne("SELECT source_key FROM items WHERE job_id=@j AND record_id=@r AND IFNULL(mapping_file,'')=IFNULL(@mf,'') AND status IN ('READY','PROCESSED') AND id<>@id",
            ("j", jobId), ("r", recordId), ("mf", rec.MappingFile), ("id", itemId));
        if (claimedBy != null && ev.Status == ItemStatus.Ready) { ev.Status = ItemStatus.Duplicate; ev.Reason = $"Record {recordId} is already matched to {claimedBy.Str("source_key")}."; }

        app.Db.Execute(@"UPDATE items SET status=@st, reason=@reason, record_id=@rid, employee_code=@emp, employee_name=@ename, doc_type=@type, doc_name=@dname,
            description=@desc, uploaded_on=@up, module_code=@mod, submodule_code=@sub, mapping_file=@mf, year=@year, dest_key=@dest,
            resolved_by=@by, resolution_note=@note WHERE id=@id",
            ("st", ev.Status), ("reason", ev.Reason ?? $"Resolved manually to record {recordId} by {user}."), ("rid", rec.RecordId), ("emp", rec.EmployeeCode),
            ("ename", rec.EmployeeName), ("type", rec.DocumentType), ("dname", rec.DocumentName), ("desc", rec.Description), ("up", rec.UploadedOn),
            ("mod", rec.ModuleCode), ("sub", rec.SubModuleCode), ("mf", rec.MappingFile), ("year", ev.Year), ("dest", ev.DestKey),
            ("by", user), ("note", note), ("id", itemId));
        app.RefreshCounts(jobId);
        app.Audit(user, role, "Ambiguity resolved", jobId, $"{f.Key} assigned to record {recordId}. Result: {ev.Status}. Note: {note}");
        return (true, ev.Status == ItemStatus.Ready ? "Resolved. The file is ready for processing." : $"Assigned, but the item is {ev.Status}: {ev.Reason}");
    }

    /// <summary>
    /// Queues FAILED items again. Each file's current version is re-read from the source (size and ETag),
    /// because a retry is an explicit decision to copy the file as it is now.
    /// </summary>
    public async Task<(int queued, int stillMissing)> RetryFailedAsync(string jobId, CancellationToken ct)
    {
        var job = app.GetJob(jobId)!;
        var src = storage.Create(AppDb.Loc(job.Str("source_json")));
        var failed = app.Db.Query("SELECT id, source_key FROM items WHERE job_id=@j AND status='FAILED' AND source_key IS NOT NULL AND dest_key IS NOT NULL", ("j", jobId));
        int queued = 0, missing = 0;
        foreach (var f in failed)
        {
            var p = await src.GetPropsAsync(f.Str("source_key")!, ct);
            if (p == null) { missing++; continue; }
            app.Db.Execute("UPDATE items SET status='READY', reason=NULL, processed_at=NULL, processed_by=NULL, started_at=NULL, source_etag=@e, source_size=@s WHERE id=@id",
                ("e", p.ETag), ("s", p.Size), ("id", f.Long("id")));
            queued++;
        }
        return (queued, missing);
    }

    /// <summary>Deletes jobs (and their files) older than the retention period.</summary>
    public void ApplyRetention(string dataPath)
    {
        var s = app.LoadSettings();
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, s.JobRetentionDays)).ToString("o");
        foreach (var j in app.Db.Query("SELECT id FROM jobs WHERE created_at < @c AND status NOT IN (@p,@s)", ("c", cutoff), ("p", JobStatus.Processing), ("s", JobStatus.Scanning)))
        {
            var id = j.Str("id")!;
            app.Db.Execute("DELETE FROM items WHERE job_id=@j", ("j", id));
            app.Db.Execute("DELETE FROM jobs WHERE id=@j", ("j", id));
            try { Directory.Delete(Path.Combine(dataPath, "jobs", id), true); } catch { }
            app.Audit("System", null, "Job purged (retention)", id, $"Older than {s.JobRetentionDays} days.");
        }
        var auditCutoff = DateTime.UtcNow.AddDays(-Math.Max(30, s.AuditRetentionDays)).ToString("o");
        app.Db.Execute("DELETE FROM audit WHERE ts < @c", ("c", auditCutoff));
    }
}
