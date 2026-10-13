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
        Ambiguous = "AMBIGUOUS", Duplicate = "DUPLICATE", Failed = "FAILED", Invalid = "INVALID";
    public static readonly string[] All = { Ready, Processed, Unmapped, Missing, Ambiguous, Duplicate, Invalid, Failed };
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
    [JsonIgnore] public int RowNumber { get; set; }
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
