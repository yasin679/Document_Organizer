using System.Globalization;
using System.Text.RegularExpressions;
using DocumentOrganizer.Domain;
using DocumentOrganizer.Infrastructure;

namespace DocumentOrganizer.Engine;

/// <summary>Reads the mapping file (.xlsx or .csv), detects headers and maps columns.</summary>
public static class MappingFile
{
    public static readonly (string Field, string Label, string[] Synonyms)[] Fields =
    {
        ("RecordId", "Record ID", new[] { "ID", "RecordID", "DocumentID", "DocID", "LogID" }),
        ("EmployeeCode", "Employee code", new[] { "EmployeeCode", "EmpCode", "EmployeeID", "EmpID", "EmployeeNo" }),
        ("EmployeeName", "Employee name", new[] { "EmployeeName", "EmpName", "FullName" }),
        ("DocumentType", "Document type", new[] { "Type", "DocumentType", "DocType", "Category" }),
        ("DocumentName", "Document name", new[] { "Name", "DocumentName", "DocName", "Title" }),
        ("Description", "Description", new[] { "Description", "Desc", "Remarks" }),
        ("UploadedOn", "Uploaded on", new[] { "UploadedOn", "UploadDate", "UploadedDate", "CreatedOn", "CreatedDate" }),
        ("FileName", "File name", new[] { "FileName", "PhysicalFileName", "File", "FilePath", "StoredFileName" }),
        ("ModuleCode", "Module", new[] { "ModuleCode", "Module" }),
        ("SubModuleCode", "Sub-module", new[] { "SubModuleCode", "SubModule" }),
        ("Year", "Year", new[] { "Year", "DocumentYear", "DocYear", "FinancialYear" }),
    };

    /// <summary>Cell values that mean "no value" in database exports (for example a literal NULL).</summary>
    public static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase) { "NULL", "N/A", "NA", "#N/A", "NONE", "NIL", "-", "--", "(null)" };

    /// <summary>Separators between several file names in one File name cell.</summary>
    public static readonly char[] FileNameSeparators = { ',', ';', '\n', '\r' };

    private static string Norm(string s) => Regex.Replace(s ?? "", @"[\s_\-\.]", "").ToLowerInvariant();

    public static IEnumerable<string[]> Rows(string path) =>
        path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? Csv.ReadRows(path) : XlsxReader.ReadRows(path, IsHeaderRow);

    /// <summary>A row that names at least 3 known columns is taken as the header row.</summary>
    private static bool IsHeaderRow(string[] row) => row.Count(c => Fields.Any(f => f.Synonyms.Any(s => Norm(s) == Norm(c)))) >= 3;

    public sealed record Inspection(int HeaderRow, string[] Headers, List<string[]> Preview, int DataRows, ColumnMap AutoMap);

    /// <summary>Finds the header row (first row in the first 20 that matches at least 3 known column names) and counts data rows.</summary>
    public static Inspection Inspect(string path)
    {
        int headerRow = -1; string[] headers = Array.Empty<string>();
        var preview = new List<string[]>();
        int index = 0, dataRows = 0;
        var firstRows = new List<string[]>();
        foreach (var row in Rows(path))
        {
            if (headerRow < 0)
            {
                firstRows.Add(row);
                if (IsHeaderRow(row)) { headerRow = index; headers = row.Select(c => c.Trim()).ToArray(); }
                else if (index >= 19) { headerRow = 0; headers = firstRows[0].Select(c => c.Trim()).ToArray(); preview.AddRange(firstRows.Skip(1).Take(5)); dataRows = firstRows.Count - 1; }
            }
            else if (row.Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                dataRows++;
                if (preview.Count < 5) preview.Add(row);
            }
            index++;
        }
        if (headerRow < 0 && firstRows.Count > 0)
        {
            headerRow = 0; headers = firstRows[0].Select(c => c.Trim()).ToArray();
            foreach (var r in firstRows.Skip(1)) { if (r.Any(c => !string.IsNullOrWhiteSpace(c))) { dataRows++; if (preview.Count < 5) preview.Add(r); } }
        }
        return new Inspection(headerRow, headers, preview, dataRows, AutoMap(headers));
    }

    public static ColumnMap AutoMap(string[] headers)
    {
        var map = new ColumnMap();
        foreach (var (field, _, syn) in Fields)
        {
            var match = syn.Select(s => headers.FirstOrDefault(h => Norm(h) == Norm(s))).FirstOrDefault(h => h != null);
            typeof(ColumnMap).GetProperty(field)!.SetValue(map, match);
        }
        return map;
    }

    public static List<string> ValidateMap(ColumnMap map, JobConfig cfg, string[] headers)
    {
        var errors = new List<string>();
        void Req(string? col, string label)
        {
            if (string.IsNullOrWhiteSpace(col)) errors.Add($"{label} column is required.");
            else if (!headers.Contains(col)) errors.Add($"Column '{col}' (for {label}) is not in the file.");
        }
        Req(map.EmployeeCode, "Employee code");
        Req(map.DocumentType, "Document type");
        Req(map.FileName, "File name");
        // With several mapping files, a file without the rule's column may use the other date column instead.
        bool hasUp = !string.IsNullOrWhiteSpace(map.UploadedOn), hasYear = !string.IsNullOrWhiteSpace(map.Year);
        if (cfg.YearRule == "UploadedOn" && !hasYear) Req(map.UploadedOn, "Uploaded on");
        if (cfg.YearRule == "YearColumn" && !hasUp) Req(map.Year, "Year");
        if (cfg.YearRule is "FileNamePattern" or "PathPattern")
        {
            try { _ = new Regex(cfg.YearPattern ?? "", RegexOptions.None, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException) { errors.Add("Year pattern is not a valid regular expression."); }
        }
        foreach (var p in typeof(ColumnMap).GetProperties())
        {
            var v = p.GetValue(map) as string;
            if (!string.IsNullOrWhiteSpace(v) && !headers.Contains(v) && !errors.Any(e => e.Contains($"'{v}'"))) errors.Add($"Column '{v}' is not in the file.");
        }
        if (!Template.IsValid(cfg.Template, out var tErr)) errors.Add(tErr!);
        else
        {
            // Every column the folder template uses must be mapped, or every item would be INVALID
            foreach (Match m in Regex.Matches(Template.Normalize(cfg.Template), @"\{(\w+)\}"))
            {
                var field = m.Groups[1].Value;
                if (field.ToLowerInvariant() is "year" or "filename" or "mappingfile" or "employeecode" or "documenttype") continue;
                var prop = typeof(ColumnMap).GetProperties().FirstOrDefault(p => p.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
                if (prop != null && string.IsNullOrWhiteSpace(prop.GetValue(map) as string))
                    errors.Add($"The folder template uses {{{prop.Name}}}, but no column is mapped to {Fields.First(f => f.Field == prop.Name).Label}.");
            }
        }
        return errors;
    }

    /// <summary>Reads all mapping records using the configured column map.</summary>
    public static IEnumerable<Candidate> ReadRecords(string path, int headerRow, string[] headers, ColumnMap map, string? mappingFileName = null)
    {
        int Idx(string? col) => string.IsNullOrWhiteSpace(col) ? -1 : Array.IndexOf(headers, col);
        int iId = Idx(map.RecordId), iEmp = Idx(map.EmployeeCode), iName = Idx(map.EmployeeName), iType = Idx(map.DocumentType),
            iDoc = Idx(map.DocumentName), iDesc = Idx(map.Description), iUp = Idx(map.UploadedOn), iFile = Idx(map.FileName),
            iMod = Idx(map.ModuleCode), iSub = Idx(map.SubModuleCode), iYear = Idx(map.Year);
        int rowNum = 0;
        foreach (var row in Rows(path))
        {
            rowNum++;
            if (rowNum - 1 <= headerRow) continue;
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            string? V(int i)
            {
                if (i < 0 || i >= row.Length || string.IsNullOrWhiteSpace(row[i])) return null;
                var v = row[i].Trim();
                return Placeholders.Contains(v) ? null : v;
            }
            yield return new Candidate
            {
                RowNumber = rowNum,
                RecordId = V(iId) ?? $"ROW-{rowNum}",
                EmployeeCode = V(iEmp), EmployeeName = V(iName), DocumentType = V(iType), DocumentName = V(iDoc),
                Description = V(iDesc), UploadedOn = V(iUp), FileName = V(iFile),
                ModuleCode = V(iMod), SubModuleCode = V(iSub), Year = V(iYear), MappingFile = mappingFileName,
                HasUploadedOnColumn = iUp >= 0, HasYearColumn = iYear >= 0
            };
        }
    }
}

public static class YearRules
{
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MMM-yy", "dd/MM/yyyy", "d/M/yyyy",
        "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "MM/dd/yyyy", "dd MMM yyyy", "d MMM yyyy", "MMM d, yyyy", "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss",
        "dd-MMM-yyyy HH:mm:ss", "yyyy/MM/dd", "yyyyMMdd"
    };

    /// <summary>Returns a 4-digit year or null with a reason.</summary>
    public static (string? Year, string? Error) Resolve(JobConfig cfg, Candidate rec, string? sourceKey)
    {
        string rule = cfg.YearRule;
        if (rule == "UploadedOn" && !rec.HasUploadedOnColumn && rec.HasYearColumn) rule = "YearColumn";
        else if (rule == "YearColumn" && !rec.HasYearColumn && rec.HasUploadedOnColumn) rule = "UploadedOn";
        string? y = rule switch
        {
            "UploadedOn" => FromDate(rec.UploadedOn),
            "YearColumn" => FromYearText(rec.Year),
            "FileNamePattern" => FromPattern(cfg.YearPattern, rec.FileName ?? (sourceKey == null ? null : Path.GetFileName(sourceKey))),
            "PathPattern" => FromPattern(cfg.YearPattern, sourceKey),
            _ => null
        };
        if (y != null && int.TryParse(y, out var n) && n >= 1980 && n <= DateTime.Now.Year + 1) return (y, null);
        var src = rule switch { "UploadedOn" => "UploadedOn", "YearColumn" => "Year column", "FileNamePattern" => "file name", _ => "path" };
        return (null, y == null ? $"Year could not be determined from {src}." : $"Year {y} from {src} is out of range.");
    }

    public static string? FromDate(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (DateTime.TryParseExact(v, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) return d.Year.ToString();
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) && oa > 20000 && oa < 80000) return DateTime.FromOADate(oa).Year.ToString();
        if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d)) return d.Year.ToString();
        var m = Regex.Match(v, @"\b(19|20)\d{2}\b");
        return m.Success ? m.Value : null;
    }

    private static string? FromYearText(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var m = Regex.Match(v, @"(19|20)\d{2}");
        return m.Success ? m.Value : null;
    }

    private static string? FromPattern(string? pattern, string? text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern)) return null;
        try
        {
            var m = Regex.Match(text, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));
            if (!m.Success) return null;
            var g = m.Groups.Count > 1 && m.Groups[^1].Success && m.Groups[^1].Value.Length == 4 ? m.Groups[^1].Value : m.Value;
            var y = Regex.Match(g, @"(19|20)\d{2}");
            return y.Success ? y.Value : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }
}

public static class Template
{
    public static readonly string[] Tokens = { "EmployeeCode", "DocumentType", "Year", "FileName", "ModuleCode", "SubModuleCode", "DocumentName", "EmployeeName", "MappingFile" };

    public static bool IsValid(string? template, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(template)) { error = "Folder template is required."; return false; }
        var t = Normalize(template);
        foreach (Match m in Regex.Matches(t, @"\{([^}]*)\}"))
            if (!Tokens.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) { error = $"Unknown token {{{m.Groups[1].Value}}} in folder template."; return false; }
        if (!t.Contains("{FileName}", StringComparison.OrdinalIgnoreCase)) { error = "Folder template must end with {FileName}."; return false; }
        if (t.Contains("..")) { error = "Folder template cannot contain '..'."; return false; }
        return true;
    }

    /// <summary>Accepts both {Token} and &lt;Token&gt; styles.</summary>
    public static string Normalize(string template) => Regex.Replace(template.Trim().Replace('\\', '/'), @"<(\w+)>", "{$1}").Trim('/');

    /// <summary>
    /// A value used in the folder template that is blank, or would disappear as a folder name ("." or ".."):
    /// either would silently put files one folder level higher. Returns the reason, or null when every value is usable.
    /// </summary>
    public static string? UnusableValue(string template, Candidate r)
    {
        var t = Normalize(template);
        foreach (Match m in Regex.Matches(t, @"\{(\w+)\}"))
        {
            var token = m.Groups[1].Value;
            string? v = token.ToLowerInvariant() switch
            {
                "employeecode" => r.EmployeeCode, "documenttype" => r.DocumentType, "modulecode" => r.ModuleCode,
                "submodulecode" => r.SubModuleCode, "documentname" => r.DocumentName, "employeename" => r.EmployeeName, _ => null
            };
            if (token.ToLowerInvariant() is "year" or "filename" or "mappingfile") continue;
            if (string.IsNullOrWhiteSpace(v)) return $"{token} is blank, but the folder template uses {{{token}}}. Fill it in, or remove the token from the template.";
            if (Sanitize(v).Length == 0) return $"{token} '{v}' cannot be used as a folder name (it is only dots or spaces).";
        }
        return null;
    }

    public static string Build(string template, Candidate r, string year, string fileName)
    {
        var t = Normalize(template);
        var segs = t.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(seg =>
        {
            bool isFile = seg.Equals("{FileName}", StringComparison.OrdinalIgnoreCase);
            var v = Regex.Replace(seg, @"\{(\w+)\}", m => m.Groups[1].Value.ToLowerInvariant() switch
            {
                "employeecode" => r.EmployeeCode ?? "",
                "documenttype" => r.DocumentType ?? "",
                "year" => year,
                "filename" => fileName,
                "modulecode" => r.ModuleCode ?? "",
                "submodulecode" => r.SubModuleCode ?? "",
                "documentname" => r.DocumentName ?? "",
                "employeename" => r.EmployeeName ?? "",
                "mappingfile" => r.MappingFile == null ? "" : Path.GetFileNameWithoutExtension(r.MappingFile),
                _ => ""
            });
            return Sanitize(v, isFile);
        }).Where(s => s.Length > 0);
        return string.Join("/", segs);
    }

    /// <summary>Makes a path segment safe for Windows folders and blob names.</summary>
    public static string Sanitize(string s, bool isFile = false)
    {
        s = Regex.Replace(s, @"[\\/:*?""<>|\x00-\x1F]", "_").Trim();
        s = s.TrimEnd('.', ' ');
        if (s.Length > 150) s = isFile ? s[..100] + s[^Math.Min(40, s.Length - 100)..] : s[..150];
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        // Windows treats a name as a device when the part before the first dot is reserved (CON, CON.txt, con.tar.gz)
        if (reserved.Contains(s.Split('.')[0].Trim().ToUpperInvariant())) s = "_" + s;
        return s;
    }
}
