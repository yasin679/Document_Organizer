namespace DocumentOrganizer.Storage;

public static class StorageProviders
{
    public const string Local = "Local", AzureBlob = "AzureBlob", S3 = "S3";
}

/// <summary>
/// A named cloud storage connection from configuration (appsettings.json or environment variables).
/// Secrets live in configuration / environment, never in the job database.
/// </summary>
public sealed class StorageConnectionOptions
{
    /// <summary>"AzureBlob" or "S3". Inferred when omitted: S3 settings (Region, ServiceUrl, AccessKeyId) mean S3, otherwise Azure Blob.</summary>
    public string? Provider { get; set; }
    public string? Description { get; set; }
    /// <summary>When true, "Load demo data" also uploads the sample files to this connection (meant for local emulators).</summary>
    public bool UseForDemo { get; set; }

    // ---------------- Azure Blob ----------------
    /// <summary>Full connection string (account key or SAS), or "UseDevelopmentStorage=true" for Azurite.</summary>
    public string? ConnectionString { get; set; }
    /// <summary>e.g. https://mystorageaccount.blob.core.windows.net</summary>
    public string? AccountUrl { get; set; }
    /// <summary>
    /// Azure: ConnectionString | Sas | ManagedIdentity | AzureCli (inferred when omitted).
    /// S3: AccessKey | Default | Environment | Profile | WebIdentity | Container | InstanceProfile (AccessKey when keys are set, otherwise Default).
    /// </summary>
    public string? Auth { get; set; }
    public string? SasToken { get; set; }
    /// <summary>Client ID of a user-assigned managed identity (optional).</summary>
    public string? ManagedIdentityClientId { get; set; }

    // ---------------- Amazon S3 / S3-compatible ----------------
    /// <summary>AWS region, e.g. ap-south-1. Defaults to AWS_REGION / AWS_DEFAULT_REGION, then us-east-1.</summary>
    public string? Region { get; set; }
    /// <summary>Custom endpoint for S3-compatible storage (MinIO, Wasabi, Cloudflare R2, etc.). Leave empty for AWS.</summary>
    public string? ServiceUrl { get; set; }
    /// <summary>Use path-style URLs (endpoint/bucket/key). Defaults to true when ServiceUrl is set, false for AWS.</summary>
    public bool? ForcePathStyle { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public string? SessionToken { get; set; }
    /// <summary>Profile in the shared credentials file (~/.aws/credentials). Defaults to AWS_PROFILE, then "default".</summary>
    public string? Profile { get; set; }

    public string ResolveProvider()
    {
        if (!string.IsNullOrWhiteSpace(Provider))
        {
            if (Provider.Equals(StorageProviders.S3, StringComparison.OrdinalIgnoreCase) || Provider.Equals("AwsS3", StringComparison.OrdinalIgnoreCase)) return StorageProviders.S3;
            if (Provider.Equals(StorageProviders.AzureBlob, StringComparison.OrdinalIgnoreCase) || Provider.Equals("Azure", StringComparison.OrdinalIgnoreCase)) return StorageProviders.AzureBlob;
            throw new InvalidOperationException($"Unknown Provider '{Provider}'. Use AzureBlob or S3.");
        }
        if (!string.IsNullOrWhiteSpace(Region) || !string.IsNullOrWhiteSpace(ServiceUrl) || !string.IsNullOrWhiteSpace(AccessKeyId)) return StorageProviders.S3;
        return StorageProviders.AzureBlob;
    }
}

/// <summary>An error response from a cloud storage service.</summary>
public class StorageRequestException(int status, string code, string message, string service)
    : Exception($"{service} returned {status} {code}: {message}")
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Stream that also disposes the HTTP response it came from.</summary>
internal sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); owner.Dispose(); } base.Dispose(disposing); }
}

internal static class StreamUtil
{
    public static async Task<int> ReadFull(Stream s, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length) { int r = await s.ReadAsync(buf.AsMemory(total), ct); if (r == 0) break; total += r; }
        return total;
    }

    /// <summary>Metadata values must be ASCII; non-ASCII values are percent-encoded.</summary>
    public static string EncodeMetaValue(string v) => v.All(c => c >= 32 && c < 127) ? v : Uri.EscapeDataString(v);
}
