using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentOrganizer.Domain;
using DocumentOrganizer.Infrastructure;
using DocumentOrganizer.Storage;

namespace DocumentOrganizer.Engine;

/// <summary>
/// Scan → match → validate. Produces one item per source file, plus one item per mapping record that
/// has no file (MISSING), is a duplicate record, or has a blank FileName. Nothing is guessed:
/// anything that cannot be placed with the configured rules becomes an exception.
/// </summary>
public sealed class Scanner(AppDb app, StorageFactory storage, ILogger<Scanner> log)
{
    public sealed class Progress { public string Phase = ""; public long Done; public long Total; }

    public static string NormKey(string name)
    {
        var s = name.Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"[\s_\-]+", "_");
        s = Regex.Replace(s, @"_?\._?", ".");
        return s;
    }

    private static string LastSegment(string f) { f = f.Replace('\\', '/').Trim(); var i = f.LastIndexOf('/'); return i >= 0 ? f[(i + 1)..] : f; }
    private static string RecPath(string f) => f.Replace('\\', '/').Trim().TrimStart('/');

    public async Task ScanAsync(string jobId, Progress progress, CancellationToken ct)
    {
        var job = app.GetJob(jobId) ?? throw new InvalidOperationException("Job not found.");
        var cfg = AppDb.Cfg(job.Str("config_json"));
        var src = storage.Create(AppDb.Loc(job.Str("source_json")));
        var mappingPath = job.Str("mapping_file") ?? throw new InvalidOperationException("Upload a mapping file first.");

        // 1. Read mapping records
        progress.Phase = "Reading mapping file";
        var insp = MappingFile.Inspect(mappingPath);
        var records = new List<Candidate>(insp.DataRows);
        foreach (var r in MappingFile.ReadRecords(mappingPath, insp.HeaderRow, insp.Headers, cfg.Columns))
        {
            records.Add(r);
            if (records.Count % 10000 == 0) { progress.Done = records.Count; ct.ThrowIfCancellationRequested(); }
        }
        progress.Done = records.Count; progress.Total = records.Count;
        log.LogInformation("{Job}: read {Count} mapping records", jobId, records.Count);

        // 2. Split records: blank FileName, duplicates, keepers
        var items = new List<ItemRow>();
        var keepers = new List<Candidate>();
        var firstByKey = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (string.IsNullOrWhiteSpace(r.FileName)) { items.Add(ItemRow.FromRecord(r, ItemStatus.Invalid, $"Blank FileName in mapping record {r.RecordId}.")); continue; }
            var key = NormKey(r.FileName!) + "|" + (r.EmployeeCode ?? "").ToUpperInvariant() + "|" + (r.DocumentType ?? "").ToUpperInvariant();
            if (firstByKey.TryGetValue(key, out var first))
                items.Add(ItemRow.FromRecord(r, ItemStatus.Duplicate, $"Duplicate mapping record: same file, employee and type as record {first.RecordId}."));
            else { firstByKey[key] = r; keepers.Add(r); }
        }

        var exact = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        var ci = new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase);
        var norm = new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        static void Add(Dictionary<string, List<Candidate>> d, string k, Candidate c) { if (!d.TryGetValue(k, out var l)) d[k] = l = new(); l.Add(c); }
        foreach (var r in keepers)
        {
            var name = LastSegment(r.FileName!);
            Add(exact, name, r);
            if (cfg.CaseInsensitiveMatch) Add(ci, name, r);
            if (cfg.NormalizedMatch) Add(norm, NormKey(name), r);
        }

        // 3. List source
        progress.Phase = "Listing source documents"; progress.Done = 0; progress.Total = 0;
        var files = new List<SourceFile>();
        await foreach (var f in src.ListAsync(ct))
        {
            if (Path.GetFileName(f.Key).StartsWith(".docorg-write-test-") || f.Key.Contains(".partial-")) continue;
            files.Add(f);
            if (files.Count % 1000 == 0) progress.Done = files.Count;
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        progress.Done = files.Count;
        log.LogInformation("{Job}: listed {Count} source files", jobId, files.Count);

        // 4. Match and validate
        progress.Phase = "Matching and validating"; progress.Done = 0; progress.Total = files.Count;
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);  // recordId -> source key
        var touched = new HashSet<string>(StringComparer.Ordinal);            // records referenced by any file (incl. ambiguous)
        var destTaken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            progress.Done++;
            if ((progress.Done & 4095) == 0) ct.ThrowIfCancellationRequested();
            var cands = Match(f, exact, ci, norm, cfg);
            foreach (var c in cands) touched.Add(c.RecordId);
            ItemRow it;
            if (cands.Count == 0)
                it = ItemRow.FromFile(f, ItemStatus.Unmapped, "No mapping record matches this file name (exact, case-insensitive or normalized).");
            else if (cands.Count > 1)
            {
                it = ItemRow.FromFile(f, ItemStatus.Ambiguous, $"{cands.Count} mapping records match this file ({string.Join(", ", cands.Take(5).Select(c => c.RecordId))}). Manual resolution required.");
                it.CandidatesJson = JsonSerializer.Serialize(cands, AppDb.Json);
                it.EmployeeCode = string.Join(" / ", cands.Select(c => c.EmployeeCode).Distinct().Take(5));
                it.DocType = string.Join(" / ", cands.Select(c => c.DocumentType).Distinct().Take(3));
            }
            else
            {
                var rec = cands[0];
                if (claimed.TryGetValue(rec.RecordId, out var other))
                    it = ItemRow.FromFileAndRecord(f, rec, ItemStatus.Duplicate, $"Duplicate physical file: record {rec.RecordId} is already matched to {other}.");
                else
                {
                    claimed[rec.RecordId] = f.Key;
                    it = Evaluate(f, rec, cfg, destTaken);
                }
            }
            items.Add(it);
        }

        // 5. Records with no file
        foreach (var r in keepers)
            if (!touched.Contains(r.RecordId))
                items.Add(ItemRow.FromRecord(r, ItemStatus.Missing, "Mapping exists but no source file with this name was found."));

        // 6. Persist
        progress.Phase = "Saving results"; progress.Done = 0; progress.Total = items.Count;
        app.Db.InTransaction(() =>
        {
            app.Db.Execute("DELETE FROM items WHERE job_id=@j", ("j", jobId));
            using var ins = app.Db.Prepare2(ItemRow.InsertSql);
            foreach (var it in items) { ins.Run(it.Args(jobId)); progress.Done++; }
        });
        app.RefreshCounts(jobId);
        log.LogInformation("{Job}: saved {Count} items", jobId, items.Count);
    }

    private static List<Candidate> Match(SourceFile f, Dictionary<string, List<Candidate>> exact, Dictionary<string, List<Candidate>> ci,
        Dictionary<string, List<Candidate>> norm, JobConfig cfg)
    {
        List<Candidate>? found = null;
        if (exact.TryGetValue(f.Name, out var e)) found = e;
        else if (cfg.CaseInsensitiveMatch && ci.TryGetValue(f.Name, out var c)) found = c;
        else if (cfg.NormalizedMatch && norm.TryGetValue(NormKey(f.Name), out var n)) found = n;
        if (found == null) return new();
        if (found.Count > 1)
        {
            // Relative path + file name narrows matches when the mapping FileName contains a path
            var byPath = found.Where(r => r.FileName!.Contains('/') || r.FileName!.Contains('\\'))
                .Where(r => ("/" + f.Key).EndsWith("/" + RecPath(r.FileName!), StringComparison.OrdinalIgnoreCase)).ToList();
            if (byPath.Count >= 1) return byPath;
        }
        return found;
    }

    /// <summary>Validates one file + record pair and builds its destination path.</summary>
    public static ItemRow Evaluate(SourceFile f, Candidate rec, JobConfig cfg, Dictionary<string, string> destTaken)
    {
        string? fail = null;
        if (string.IsNullOrWhiteSpace(rec.EmployeeCode)) fail = $"Blank EmployeeCode in mapping record {rec.RecordId}.";
        else if (string.IsNullOrWhiteSpace(rec.DocumentType)) fail = $"Blank Type / Document Type in mapping record {rec.RecordId}.";
        else if (string.Equals(f.AccessTier, "Archive", StringComparison.OrdinalIgnoreCase)) fail = "Source inaccessible: the object is in an archive tier (Azure Archive, S3 Glacier Flexible Retrieval or Deep Archive) and is not restored automatically.";
        else if (f.Size == 0) fail = "Empty (0-byte) file: unsupported or corrupt.";
        var (year, yErr) = YearRules.Resolve(cfg, rec, f.Key);
        if (fail == null && year == null && cfg.YearMandatory) fail = yErr;
        var it = ItemRow.FromFileAndRecord(f, rec, fail == null ? ItemStatus.Ready : ItemStatus.Invalid, fail);
        it.Year = year ?? (cfg.YearMandatory ? null : "Unknown Year");
        if (fail != null) return it;
        it.DestKey = Template.Build(cfg.Template, rec, it.Year!, f.Name);
        if (destTaken.TryGetValue(it.DestKey, out var otherSrc))
        {
            it.Status = ItemStatus.Duplicate;
            it.Reason = $"Destination path collision: {otherSrc} already maps to {it.DestKey}.";
        }
        else destTaken[it.DestKey] = f.Key;
        return it;
    }
}

public sealed class ItemRow
{
    public string? SourceKey, FileName, SourceEtag, SourceMd5, AccessTier, RecordId, EmployeeCode, EmployeeName, DocType, DocName,
        Description, UploadedOn, ModuleCode, SubModuleCode, Year, DestKey, Status, Reason, CandidatesJson;
    public long? SourceSize;

    public const string InsertSql = @"INSERT INTO items(job_id,source_key,file_name,source_size,source_etag,source_md5,access_tier,record_id,employee_code,employee_name,
        doc_type,doc_name,description,uploaded_on,module_code,submodule_code,year,dest_key,status,reason,candidates_json)
        VALUES(@job,@sk,@fn,@sz,@et,@md5,@tier,@rid,@emp,@ename,@type,@dname,@desc,@up,@mod,@sub,@year,@dest,@st,@reason,@cand)";

    public (string, object?)[] Args(string jobId) => new (string, object?)[]
    {
        ("job", jobId), ("sk", SourceKey), ("fn", FileName), ("sz", SourceSize), ("et", SourceEtag), ("md5", SourceMd5), ("tier", AccessTier),
        ("rid", RecordId), ("emp", EmployeeCode), ("ename", EmployeeName), ("type", DocType), ("dname", DocName), ("desc", Description),
        ("up", UploadedOn), ("mod", ModuleCode), ("sub", SubModuleCode), ("year", Year), ("dest", DestKey),
        ("st", Status), ("reason", Reason), ("cand", CandidatesJson)
    };

    public static ItemRow FromFile(SourceFile f, string status, string? reason) => new()
    {
        SourceKey = f.Key, FileName = f.Name, SourceSize = f.Size, SourceEtag = f.ETag, SourceMd5 = f.ContentMd5, AccessTier = f.AccessTier,
        Status = status, Reason = reason
    };

    public static ItemRow FromRecord(Candidate r, string status, string? reason) => new()
    {
        FileName = r.FileName, RecordId = r.RecordId, EmployeeCode = r.EmployeeCode, EmployeeName = r.EmployeeName, DocType = r.DocumentType,
        DocName = r.DocumentName, Description = r.Description, UploadedOn = r.UploadedOn, ModuleCode = r.ModuleCode,
        SubModuleCode = r.SubModuleCode, Status = status, Reason = reason
    };

    public static ItemRow FromFileAndRecord(SourceFile f, Candidate r, string status, string? reason)
    {
        var it = FromRecord(r, status, reason);
        it.SourceKey = f.Key; it.FileName = f.Name; it.SourceSize = f.Size; it.SourceEtag = f.ETag; it.SourceMd5 = f.ContentMd5; it.AccessTier = f.AccessTier;
        return it;
    }
}
