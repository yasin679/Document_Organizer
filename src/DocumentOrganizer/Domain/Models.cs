using System.Text.Json.Serialization;

namespace DocumentOrganizer.Domain;

public static class Roles
{
    public const string Administrator = "Administrator";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";
    public static readonly string[] All = { Administrator, Operator, Viewer };
}

/// <summary>Item statuses. READY..FAILED come from the PRD; INVALID groups validation-rule failures.</summary>
public static class ItemStatus
{
    public const string Ready = "READY", Processed = "PROCESSED", Unmapped = "UNMAPPED", Missing = "MISSING",
        Ambiguous = "AMBIGUOUS", Duplicate = "DUPLICATE", Failed = "FAILED", Invalid = "INVALID", Excluded = "EXCLUDED";
    public static readonly string[] All = { Ready, Processed, Unmapped, Missing, Ambiguous, Duplicate, Invalid, Failed, Excluded };
    /// <summary>Exceptions a person can exclude (set aside with a note). EXCLUDED items are not copied and not counted as exceptions.</summary>
    public static readonly string[] Excludable = { Unmapped, Missing, Ambiguous, Duplicate, Invalid, Failed };
}

public static class JobStatus
{
    public const string Draft = "Draft", Scanning = "Scanning", AwaitingApproval = "Awaiting approval",
        Processing = "Processing", Paused = "Paused", Completed = "Completed",
        CompletedWithExceptions = "Completed with exceptions", Failed = "Failed";
}

public sealed class Location
{
    /// <summary>"Local", "AzureBlob" or "S3".</summary>
    public string Type { get; set; } = "Local";
    public string? Path { get; set; }            // Local
    public string? Connection { get; set; }      // AzureBlob / S3: name of a configured storage connection
    /// <summary>Azure Blob container or S3 bucket.</summary>
    public string? Container { get; set; }
    public string? Prefix { get; set; }          // AzureBlob / S3 (virtual folder)

    public string Describe() => Type switch
    {
        "Local" => $"Local · {Path}",
        "S3" => $"Amazon S3 · {Connection}/{Container}/{(Prefix ?? "").TrimStart('/')}",
        _ => $"Azure Blob · {Connection}/{Container}/{(Prefix ?? "").TrimStart('/')}"
    };
}

/// <summary>Maps application fields to the column names found in the mapping file.</summary>
public sealed class ColumnMap
{
    public string? RecordId { get; set; }
    public string? EmployeeCode { get; set; }
    public string? EmployeeName { get; set; }
    public string? DocumentType { get; set; }
    public string? DocumentName { get; set; }
    public string? Description { get; set; }
    public string? UploadedOn { get; set; }
    public string? FileName { get; set; }
    public string? ModuleCode { get; set; }
    public string? SubModuleCode { get; set; }
    public string? Year { get; set; }
}

public sealed class JobConfig
{
    public ColumnMap Columns { get; set; } = new();
    /// <summary>UploadedOn | YearColumn | FileNamePattern | PathPattern</summary>
    public string YearRule { get; set; } = "UploadedOn";
    public string? YearPattern { get; set; } = @"(19|20)\d{2}";
    public bool YearMandatory { get; set; } = true;
    public string Template { get; set; } = "{EmployeeCode}/{DocumentType}/{Year}/{FileName}";
    /// <summary>Skip | Rename | CompareHash | Overwrite</summary>
    public string DuplicateRule { get; set; } = "Skip";
    /// <summary>None | Sha256 | Md5</summary>
    public string Integrity { get; set; } = "Sha256";
    public int BatchSize { get; set; } = 5000;
    public bool CaseInsensitiveMatch { get; set; } = true;
    public bool NormalizedMatch { get; set; } = true;
    public bool KeepBlobMetadata { get; set; } = true;
    /// <summary>A File name cell that lists several files (separated by commas or semicolons) becomes one entry per file.</summary>
    public bool SplitMultipleFileNames { get; set; } = true;
    /// <summary>When one file matches several mapping records: Flag (AMBIGUOUS, a person decides) | KeepAll (one copy per record).</summary>
    public string AmbiguousRule { get; set; } = "Flag";
    /// <summary>Column mapping per uploaded mapping file (key = mapping file ID). Falls back to <see cref="Columns"/>.</summary>
    public Dictionary<string, ColumnMap>? FileColumns { get; set; }

    public ColumnMap ColumnsFor(string mappingId) =>
        FileColumns != null && FileColumns.TryGetValue(mappingId, out var m) ? m : Columns;
}

public sealed class Candidate
{
    public string RecordId { get; set; } = "";
    public string? EmployeeCode { get; set; }
    public string? EmployeeName { get; set; }
    public string? DocumentType { get; set; }
    public string? DocumentName { get; set; }
    public string? Description { get; set; }
    public string? UploadedOn { get; set; }
    public string? FileName { get; set; }
    public string? ModuleCode { get; set; }
    public string? SubModuleCode { get; set; }
    public string? Year { get; set; }
    /// <summary>Name of the mapping file this record came from.</summary>
    public string? MappingFile { get; set; }
    [JsonIgnore] public int RowNumber { get; set; }
    /// <summary>Unique within one scan; record IDs can repeat across (or even within) mapping files.</summary>
    [JsonIgnore] public int Seq { get; set; }
    /// <summary>The record's mapping file has an Uploaded on column / a Year column (files in one job can differ).</summary>
    public bool HasUploadedOnColumn { get; set; } = true;
    public bool HasYearColumn { get; set; } = true;

    public Candidate CloneWithFile(string fileName, string recordId) => new()
    {
        RecordId = recordId, EmployeeCode = EmployeeCode, EmployeeName = EmployeeName, DocumentType = DocumentType, DocumentName = DocumentName,
        Description = Description, UploadedOn = UploadedOn, FileName = fileName, ModuleCode = ModuleCode, SubModuleCode = SubModuleCode,
        Year = Year, MappingFile = MappingFile, RowNumber = RowNumber, HasUploadedOnColumn = HasUploadedOnColumn, HasYearColumn = HasYearColumn
    };
}

/// <summary>One uploaded mapping file of a job. A job can have several; their records are combined.</summary>
public sealed class MappingSource
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public int Rows { get; set; }
    public string Sha256 { get; set; } = "";
    public int HeaderRow { get; set; }
    public string[] Headers { get; set; } = Array.Empty<string>();
    public List<string[]> Preview { get; set; } = new();
    public string? UploadedBy { get; set; }
    public string? UploadedAt { get; set; }
}

public sealed class SourceFile
{
    /// <summary>Path relative to the source root, always with '/' separators.</summary>
    public string Key { get; init; } = "";
    public string Name => Key.Contains('/') ? Key[(Key.LastIndexOf('/') + 1)..] : Key;
    public long Size { get; init; }
    public string? ETag { get; init; }
    public string? ContentMd5 { get; init; }
    public string? AccessTier { get; init; }
    public DateTimeOffset? LastModified { get; init; }
}
