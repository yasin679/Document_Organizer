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
    public sealed class Progress
    {
        public string Phase = ""; public long Done; public long Total;
        public readonly DateTime Started = DateTime.UtcNow;
        public DateTime PhaseStarted = DateTime.UtcNow;
        public RateMeter Meter = new();
        /// <summary>Starts a new step: its own speed and estimate.</summary>
        public void SetPhase(string phase, long total) { Phase = phase; Done = 0; Total = total; PhaseStarted = DateTime.UtcNow; Meter = new RateMeter(); }
    }

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
        var mappings = AppDb.Mappings(job);
        if (mappings.Count == 0) throw new InvalidOperationException("Upload a mapping file first.");

        // 1. Read the records of every mapping file
        progress.SetPhase(mappings.Count == 1 ? "Reading mapping file" : $"Reading {mappings.Count} mapping files", mappings.Sum(m => m.Rows));
        var records = new List<Candidate>(progress.Total > 0 ? (int)progress.Total : 1000);
        foreach (var m in mappings)
        {
            var insp = MappingFile.Inspect(m.Path);
            foreach (var r in MappingFile.ReadRecords(m.Path, insp.HeaderRow, insp.Headers, cfg.ColumnsFor(m.Id), m.OriginalName))
            {
                records.Add(r);
                if (records.Count % 10000 == 0) { progress.Done = records.Count; ct.ThrowIfCancellationRequested(); }
            }
        }
        progress.Done = records.Count; progress.Total = records.Count;
        log.LogInformation("{Job}: read {Count} mapping records from {Files} file(s)", jobId, records.Count, mappings.Count);

        // 2. List source (before matching, so a File name cell with several names can be told apart from one file whose name has a comma)
        progress.SetPhase("Listing source documents", 0);
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
        var sourceNames = new HashSet<string>(files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        // 3. One entry per file: "a.pdf,b.pdf" in one record becomes two records (ROW-68/1, ROW-68/2)
        int split = 0;
        if (cfg.SplitMultipleFileNames)
        {
            var expanded = new List<Candidate>(records.Count);
            foreach (var r in records)
            {
                var parts = SplitFileNames(r.FileName, sourceNames);
                if (parts == null) { expanded.Add(r); continue; }
                if (parts.Count == 0) { r.FileName = null; expanded.Add(r); continue; }   // only placeholders: blank
                split++;
                for (int i = 0; i < parts.Count; i++) expanded.Add(r.CloneWithFile(parts[i], $"{r.RecordId}/{i + 1}"));
            }
            records = expanded;
            if (split > 0) log.LogInformation("{Job}: {Split} mapping records listed several files and were split", jobId, split);
        }
        for (int i = 0; i < records.Count; i++) records[i].Seq = i;

        // 4. Blank file names and repeated records. A repeated record is decided after matching:
        //    DUPLICATE if the first record found its file, otherwise MISSING like the first one.
        var items = new List<ItemRow>();
        var keepers = new List<Candidate>();
        var repeats = new List<(Candidate Rec, Candidate First)>();
        var firstByKey = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        bool multi = mappings.Count > 1;
        string Ref(Candidate c) => multi && c.MappingFile != null ? $"{c.RecordId} in {c.MappingFile}" : c.RecordId;
        foreach (var r in records)
        {
            if (string.IsNullOrWhiteSpace(r.FileName)) { items.Add(ItemRow.FromRecord(r, ItemStatus.Invalid, $"Blank FileName in mapping record {Ref(r)}.")); continue; }
            var key = NormKey(r.FileName!) + "|" + (r.EmployeeCode ?? "").ToUpperInvariant() + "|" + (r.DocumentType ?? "").ToUpperInvariant();
            if (firstByKey.TryGetValue(key, out var first)) repeats.Add((r, first));
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

        // 5. Match and validate
        progress.SetPhase("Matching and validating", files.Count);
        var claimed = new Dictionary<int, string>();      // record Seq -> source key
        var touched = new HashSet<int>();                  // records referenced by any file (incl. ambiguous)
        var destTaken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            progress.Done++;
            if ((progress.Done & 4095) == 0) ct.ThrowIfCancellationRequested();
            var cands = Match(f, exact, ci, norm, cfg);
            foreach (var c in cands) touched.Add(c.Seq);
            ItemRow it;
            if (cands.Count == 0)
                it = ItemRow.FromFile(f, ItemStatus.Unmapped, "No mapping record matches this file name (exact, case-insensitive or normalized).");
            else if (cands.Count > 1 && cfg.AmbiguousRule == "KeepAll")
            {
                // Rule "Keep all": the file is copied once for every record that names it, each to its own destination,
                // and every copy is its own item (and its own row in the reports).
                foreach (var rec in cands)
                {
                    ItemRow k;
                    if (claimed.TryGetValue(rec.Seq, out var other))
                        k = ItemRow.FromFileAndRecord(f, rec, ItemStatus.Duplicate, $"Duplicate physical file: record {Ref(rec)} is already matched to {other}.");
                    else
                    {
                        claimed[rec.Seq] = f.Key;
                        k = Evaluate(f, rec, cfg, destTaken);
                        if (k.Status == ItemStatus.Ready)
                            k.Reason = $"File matches {cands.Count} mapping records ({string.Join(", ", cands.Take(5).Select(Ref))}); kept for each (rule: keep all).";
                    }
                    items.Add(k);
                }
                continue;
            }
            else if (cands.Count > 1)
            {
                it = ItemRow.FromFile(f, ItemStatus.Ambiguous, $"{cands.Count} mapping records match this file ({string.Join(", ", cands.Take(5).Select(Ref))}). Manual resolution required.");
                it.CandidatesJson = JsonSerializer.Serialize(cands, AppDb.Json);
                it.EmployeeCode = string.Join(" / ", cands.Select(c => c.EmployeeCode).Distinct().Take(5));
                it.DocType = string.Join(" / ", cands.Select(c => c.DocumentType).Distinct().Take(3));
                it.MappingFile = string.Join(" / ", cands.Select(c => c.MappingFile).Distinct().Take(3));
            }
            else
            {
                var rec = cands[0];
                if (claimed.TryGetValue(rec.Seq, out var other))
                    it = ItemRow.FromFileAndRecord(f, rec, ItemStatus.Duplicate, $"Duplicate physical file: record {Ref(rec)} is already matched to {other}.");
                else
                {
                    claimed[rec.Seq] = f.Key;
                    it = Evaluate(f, rec, cfg, destTaken);
                }
            }
            items.Add(it);
        }

        // 6. Records with no file, and repeated records
        foreach (var r in keepers)
            if (!touched.Contains(r.Seq))
                items.Add(ItemRow.FromRecord(r, ItemStatus.Missing, "Mapping exists but no source file with this name was found."));
        foreach (var (r, first) in repeats)
            items.Add(touched.Contains(first.Seq)
                ? ItemRow.FromRecord(r, ItemStatus.Duplicate, $"Duplicate mapping record: same file, employee and type as record {Ref(first)}.")
                : ItemRow.FromRecord(r, ItemStatus.Missing, $"Mapping exists but no source file with this name was found (repeats record {Ref(first)}, which is also missing)."));

        // 7. Items that a person excluded before (same file or record, same status) are set aside again
        Exclusions.ApplyRemembered(app, jobId, items);

        // 8. Persist
        progress.SetPhase("Saving results", items.Count);
        app.Db.InTransaction(() =>
        {
            app.Db.Execute("DELETE FROM items WHERE job_id=@j", ("j", jobId));
            using var ins = app.Db.Prepare2(ItemRow.InsertSql);
            foreach (var it in items) { ins.Run(it.Args(jobId)); progress.Done++; }
        });
        app.RefreshCounts(jobId);
        log.LogInformation("{Job}: saved {Count} items", jobId, items.Count);
    }

    /// <summary>
    /// Returns the file names when a File name cell lists several files; an empty list when the cell holds only
    /// separators and placeholders (treated as blank); or null when the cell is one file name.
    /// A run of text that is exactly the name of a source file is kept together, so real names containing a comma
    /// survive, even next to other names ("report, final.pdf,x.pdf" gives "report, final.pdf" and "x.pdf").
    /// </summary>
    public static List<string>? SplitFileNames(string? fileName, HashSet<string> sourceNames)
    {
        var seps = MappingFile.FileNameSeparators;
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(seps) < 0) return null;
        var whole = fileName.Trim();
        if (IsSourceName(whole, sourceNames)) return null;
        var tokens = new List<(string Text, char Sep)>();
        int start = 0;
        for (int i = 0; i < whole.Length; i++)
            if (Array.IndexOf(seps, whole[i]) >= 0) { tokens.Add((whole[start..i], whole[i])); start = i + 1; }
        tokens.Add((whole[start..], '\0'));
        string Join(int from, int to)
        {
            var sb = new StringBuilder();
            for (int k = from; k <= to; k++) { sb.Append(tokens[k].Text); if (k < to) sb.Append(tokens[k].Sep); }
            return sb.ToString().Trim();
        }
        var parts = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            int to = i;
            for (int j = Math.Min(tokens.Count - 1, i + 4); j > i; j--)
                if (IsSourceName(Join(i, j), sourceNames)) { to = j; break; }
            var p = Join(i, to);
            if (p.Length > 0 && !MappingFile.Placeholders.Contains(p) && !parts.Contains(p, StringComparer.OrdinalIgnoreCase)) parts.Add(p);
            i = to;
        }
        return parts.Count == 1 && parts[0] == whole ? null : parts;
    }

    /// <summary>True when the text is a source file name (optionally with a folder path) and all its separators are inside the name.</summary>
    private static bool IsSourceName(string s, HashSet<string> sourceNames)
    {
        var last = LastSegment(s);
        return last.Length > 0 && sourceNames.Contains(last) && s[..^last.Length].IndexOfAny(MappingFile.FileNameSeparators) < 0;
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
        else if (Template.UnusableValue(cfg.Template, rec) is string bad) fail = $"Mapping record {rec.RecordId}: {bad}";
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
        Description, UploadedOn, ModuleCode, SubModuleCode, Year, DestKey, Status, Reason, CandidatesJson, MappingFile,
        ExcludedFrom, ExcludedBy, ExcludedAt, ExclusionNote;
    public long? SourceSize;

    public const string InsertSql = @"INSERT INTO items(job_id,source_key,file_name,source_size,source_etag,source_md5,access_tier,record_id,employee_code,employee_name,
        doc_type,doc_name,description,uploaded_on,module_code,submodule_code,year,dest_key,status,reason,candidates_json,mapping_file,
        excluded_from,excluded_by,excluded_at,exclusion_note)
        VALUES(@job,@sk,@fn,@sz,@et,@md5,@tier,@rid,@emp,@ename,@type,@dname,@desc,@up,@mod,@sub,@year,@dest,@st,@reason,@cand,@mf,@xf,@xb,@xa,@xn)";

    public (string, object?)[] Args(string jobId) => new (string, object?)[]
    {
        ("job", jobId), ("sk", SourceKey), ("fn", FileName), ("sz", SourceSize), ("et", SourceEtag), ("md5", SourceMd5), ("tier", AccessTier),
        ("rid", RecordId), ("emp", EmployeeCode), ("ename", EmployeeName), ("type", DocType), ("dname", DocName), ("desc", Description),
        ("up", UploadedOn), ("mod", ModuleCode), ("sub", SubModuleCode), ("year", Year), ("dest", DestKey),
        ("st", Status), ("reason", Reason), ("cand", CandidatesJson), ("mf", MappingFile),
        ("xf", ExcludedFrom), ("xb", ExcludedBy), ("xa", ExcludedAt), ("xn", ExclusionNote)
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
        SubModuleCode = r.SubModuleCode, MappingFile = r.MappingFile, Status = status, Reason = reason
    };

    public static ItemRow FromFileAndRecord(SourceFile f, Candidate r, string status, string? reason)
    {
        var it = FromRecord(r, status, reason);
        it.SourceKey = f.Key; it.FileName = f.Name; it.SourceSize = f.Size; it.SourceEtag = f.ETag; it.SourceMd5 = f.ContentMd5; it.AccessTier = f.AccessTier;
        return it;
    }
}
