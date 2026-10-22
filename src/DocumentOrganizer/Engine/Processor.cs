using System.Collections.Concurrent;
using DocumentOrganizer.Domain;
using DocumentOrganizer.Infrastructure;
using DocumentOrganizer.Storage;

namespace DocumentOrganizer.Engine;

/// <summary>
/// Copies READY items in batches. Source documents are never modified or deleted.
/// Successful items are not processed again; failed items can be retried.
/// </summary>
public sealed class Processor(AppDb app, StorageFactory storage, ILogger<Processor> log)
{
    private sealed record Result(long Id, string Status, string? Reason, string? DestKey, string? SrcHash, string? DstHash, string? Method);

    public async Task RunAsync(string jobId, string user, CancellationToken ct, CopyProgress? live = null)
    {
        live ??= new CopyProgress();
        var job = app.GetJob(jobId) ?? throw new InvalidOperationException("Job not found.");
        var cfg = AppDb.Cfg(job.Str("config_json"));
        var settings = app.LoadSettings();
        var src = storage.Create(AppDb.Loc(job.Str("source_json")));
        var dst = storage.Create(AppDb.Loc(job.Str("dest_json")));
        bool overwriteAllowed = cfg.DuplicateRule == "Overwrite" && settings.AllowOverwrite;
        int dop = Math.Clamp(settings.MaxParallelCopies, 1, 64);
        int batchSize = Math.Clamp(cfg.BatchSize, 100, 50000);

        long ready = Convert.ToInt64(app.Db.Scalar("SELECT COUNT(*) FROM items WHERE job_id=@j AND status='READY'", ("j", jobId)) ?? 0L);
        long readyBytes = Convert.ToInt64(app.Db.Scalar("SELECT COALESCE(SUM(source_size),0) FROM items WHERE job_id=@j AND status='READY'", ("j", jobId)) ?? 0L);
        long done0 = Convert.ToInt64(app.Db.Scalar("SELECT COUNT(*) FROM items WHERE job_id=@j AND processed_at IS NOT NULL", ("j", jobId)) ?? 0L);
        app.UpdateJob(jobId, ("progress_total", ready + done0), ("progress_done", done0));
        long done = done0;
        // Live figures for the screen: speed and time left are measured on this run only (files and bytes)
        live.Total = ready + done0; live.Done = done0; live.BytesTotal = readyBytes; live.BytesDone = 0;
        live.Meter = new RateMeter(done0, 0);
        foreach (var r in app.Db.Query("SELECT status, COUNT(*) n FROM items WHERE job_id=@j AND processed_at IS NOT NULL GROUP BY status", ("j", jobId)))
        {
            if (r.Str("status") == ItemStatus.Processed) live.Copied = r.Long("n");
            else if (r.Str("status") == ItemStatus.Duplicate) live.Skipped = r.Long("n");
            else if (r.Str("status") == ItemStatus.Failed) live.Failed = r.Long("n");
        }

        while (!ct.IsCancellationRequested)
        {
            var batch = app.Db.Query("SELECT * FROM items WHERE job_id=@j AND status='READY' ORDER BY id LIMIT @n", ("j", jobId), ("n", batchSize));
            if (batch.Count == 0) break;
            // Mark the batch as started, so a resume after a crash can recognise files that were copied but not yet recorded.
            app.Db.Execute("UPDATE items SET started_at=@t WHERE job_id=@j AND status='READY' AND id BETWEEN @a AND @b",
                ("t", DateTime.UtcNow.ToString("o")), ("j", jobId), ("a", batch[0].Long("id")), ("b", batch[^1].Long("id")));
            var pending = new List<Result>();
            var gate = new object();
            var lastFlush = DateTime.UtcNow;
            void Flush(bool force)
            {
                List<Result> toWrite;
                lock (gate)
                {
                    // Save results every 250 files or every 2 seconds, whichever comes first, so progress on screen moves steadily
                    if (!force && pending.Count < 250 && (DateTime.UtcNow - lastFlush).TotalSeconds < 2) return;
                    toWrite = pending.Where(r => r.Status != ItemStatus.Ready).ToList();
                    pending.Clear();
                    lastFlush = DateTime.UtcNow;
                }
                if (toWrite.Count == 0) return;
                var now = DateTime.UtcNow.ToString("o");
                app.Db.InTransaction(() =>
                {
                    using var upd = app.Db.Prepare2(@"UPDATE items SET status=@st, reason=@reason, dest_key=COALESCE(@dest,dest_key), source_hash=@sh, dest_hash=@dh,
                        integrity_method=@m, processed_at=@at, processed_by=@by, attempts=attempts+1 WHERE id=@id");
                    foreach (var r in toWrite)
                        upd.Run(("st", r.Status), ("reason", r.Reason), ("dest", r.DestKey), ("sh", r.SrcHash), ("dh", r.DstHash), ("m", r.Method), ("at", now), ("by", user), ("id", r.Id));
                });
                Interlocked.Add(ref done, toWrite.Count);
                app.UpdateJob(jobId, ("progress_done", Interlocked.Read(ref done)));
            }
            try
            {
                await Parallel.ForEachAsync(batch, new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct }, async (item, token) =>
                {
                    var r = await ProcessOne(jobId, item, cfg, src, dst, overwriteAllowed, token);
                    lock (gate) pending.Add(r);
                    if (r.Status != ItemStatus.Ready)
                    {
                        Interlocked.Increment(ref live.Done);
                        Interlocked.Add(ref live.BytesDone, item.LongN("source_size") ?? 0);
                        if (r.Status == ItemStatus.Processed) Interlocked.Increment(ref live.Copied);
                        else if (r.Status == ItemStatus.Duplicate) Interlocked.Increment(ref live.Skipped);
                        else if (r.Status == ItemStatus.Failed) Interlocked.Increment(ref live.Failed);
                        live.Meter.Mark(Interlocked.Read(ref live.Done), Interlocked.Read(ref live.BytesDone));
                    }
                    Flush(false);
                });
            }
            catch (OperationCanceledException) { /* pause requested: persist what finished */ }
            Flush(true);
            app.RefreshCounts(jobId);
            log.LogInformation("{Job}: batch finished, {Done}/{Total}", jobId, Interlocked.Read(ref done), ready + done0);
        }
    }

    private async Task<Result> ProcessOne(string jobId, Row item, JobConfig cfg, IDocumentStore src, IDocumentStore dst, bool overwriteAllowed, CancellationToken ct)
    {
        long id = item.Long("id");
        var key = item.Str("source_key")!;
        var destKey = item.Str("dest_key")!;
        var etag = item.Str("source_etag");
        // Cloud destinations get traceability metadata (and keep the source object's own metadata).
        var meta = cfg.KeepBlobMetadata && dst is not LocalStore ? new Dictionary<string, string>
        {
            ["docorgjobid"] = jobId, ["docorgrecordid"] = item.Str("record_id") ?? "", ["docorgemployeecode"] = item.Str("employee_code") ?? "",
        } : null;
        try
        {
            bool overwrite = false;
            var existing = await dst.GetPropsAsync(destKey, ct);
            if (existing != null && item.Str("started_at") != null)
            {
                // This file was in a batch that was interrupted (restart or crash). If the destination already holds
                // an identical copy, it was copied by this job before the interruption: record it as processed.
                var sh0 = await Hashing.HashAsync(src, key, "Sha256", ct);
                var dh0 = await Hashing.HashAsync(dst, destKey, "Sha256", ct);
                if (sh0 == dh0)
                    return new(id, ItemStatus.Processed, "Copied before an interruption; verified identical at destination (SHA-256) on resume.", destKey, sh0, dh0, "Sha256");
            }
            if (existing != null)
            {
                switch (cfg.DuplicateRule)
                {
                    case "Overwrite" when overwriteAllowed: overwrite = true; break;
                    case "Overwrite": return new(id, ItemStatus.Duplicate, "Destination already exists. Overwrite is not permitted by the administrator; skipped.", null, null, null, null);
                    case "Rename": destKey = await FreeName(dst, destKey, ct); break;
                    case "CompareHash":
                        var sh = await Hashing.HashAsync(src, key, "Sha256", ct);
                        var dh = await Hashing.HashAsync(dst, destKey, "Sha256", ct);
                        if (sh == dh) return new(id, ItemStatus.Duplicate, "Identical file already at destination (SHA-256 match). Skipped.", null, sh, dh, "Sha256");
                        destKey = await FreeName(dst, destKey, ct); break;
                    default: return new(id, ItemStatus.Duplicate, "Destination already exists. Skipped and reported.", null, null, null, null);
                }
            }

            if (meta != null && src is not LocalStore)
            {
                // keep the source object's own metadata, then add traceability fields
                var sp = await src.GetPropsAsync(key, ct);
                if (sp != null) foreach (var (k, v) in sp.Metadata) meta.TryAdd(k, v);
            }

            string? srcHash = null, dstHash = null, method = null;
            bool verify = !cfg.Integrity.Equals("None", StringComparison.OrdinalIgnoreCase);
            // Folder to folder with an integrity check: copy by streaming and hash the source while it is read,
            // so each source file is read once instead of twice (copy, then hash). Cloud server-side copies are unchanged.
            bool hashWhileCopying = verify && src is LocalStore && dst is LocalStore;
            bool serverSide = !hashWhileCopying && await dst.TryServerCopyAsync(src, key, etag, destKey, meta, overwrite, ct);
            if (!serverSide)
            {
                var sp = await src.GetPropsAsync(key, ct) ?? throw new FileNotFoundException("Source file no longer exists.");
                if (etag != null && sp.ETag != null && sp.ETag.Trim('"') != etag.Trim('"')) throw new SourceChangedException(key);
                await using var s = await src.OpenReadAsync(key, ct);
                if (verify)
                {
                    using var hs = new HashingReadStream(s, cfg.Integrity);
                    await dst.WriteAsync(destKey, hs, meta, overwrite, ct);
                    srcHash = hs.Result();
                }
                else await dst.WriteAsync(destKey, s, meta, overwrite, ct);
            }

            if (verify)
            {
                method = cfg.Integrity;
                srcHash ??= await Hashing.HashAsync(src, key, method, ct);
                dstHash = await Hashing.HashAsync(dst, destKey, method, ct);
                if (srcHash != dstHash)
                    return new(id, ItemStatus.Failed, $"Integrity check failed: {method} of destination does not match source.", destKey, srcHash, dstHash, method);
            }
            return new(id, ItemStatus.Processed, serverSide || hashWhileCopying ? null : "Copied by streaming (server-side copy not available for this source/destination pair).", destKey, srcHash, dstHash, method);
        }
        catch (DestinationExistsException) { return new(id, ItemStatus.Duplicate, "Destination already exists. Skipped and reported.", null, null, null, null); }
        catch (SourceChangedException) { return new(id, ItemStatus.Failed, "Source file changed after the scan (ETag mismatch), so it was not copied. Retry copies its current version.", null, null, null, null); }
        catch (FileNotFoundException) { return new(id, ItemStatus.Failed, "Source file no longer exists.", null, null, null, null); }
        catch (StorageRequestException ex) when (ex.Status == 404) { return new(id, ItemStatus.Failed, "Source object, container or bucket not found (404).", null, null, null, null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(id, ItemStatus.Ready, null, null, null, null, null); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "{Job}: item {Id} failed", jobId, id);
            return new(id, ItemStatus.Failed, Short(ex.Message), null, null, null, null);
        }
    }

    private static string Short(string s) => s.Length > 400 ? s[..400] : s;

    private static async Task<string> FreeName(IDocumentStore dst, string key, CancellationToken ct)
    {
        var dir = key.Contains('/') ? key[..(key.LastIndexOf('/') + 1)] : "";
        var file = key[dir.Length..];
        var stem = Path.GetFileNameWithoutExtension(file); var ext = Path.GetExtension(file);
        for (int i = 1; i <= 999; i++)
        {
            var candidate = $"{dir}{stem}_dup{i}{ext}";
            if (await dst.GetPropsAsync(candidate, ct) == null) return candidate;
        }
        throw new InvalidOperationException("Could not find a free name with suffix _dup1.._dup999.");
    }
}
