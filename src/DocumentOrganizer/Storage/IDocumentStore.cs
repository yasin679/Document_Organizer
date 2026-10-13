using System.Security.Cryptography;
using DocumentOrganizer.Domain;

namespace DocumentOrganizer.Storage;

public sealed record FileProps(long Size, string? ETag, string? ContentMd5, IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// A place where documents live: a local folder, an Azure Blob container + prefix, or an S3 bucket + prefix.
/// Keys are paths relative to the root, always using '/' as separator.
/// </summary>
public interface IDocumentStore
{
    string Kind { get; }
    string Describe { get; }
    IAsyncEnumerable<SourceFile> ListAsync(CancellationToken ct);
    Task<FileProps?> GetPropsAsync(string key, CancellationToken ct);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);
    /// <summary>Writes content. Throws DestinationExistsException when overwrite is false and the key exists.</summary>
    Task WriteAsync(string key, Stream content, IDictionary<string, string>? metadata, bool overwrite, CancellationToken ct);
    /// <summary>Copies without passing content through this application when the store supports it. Returns false when not possible.</summary>
    Task<bool> TryServerCopyAsync(IDocumentStore source, string sourceKey, string? sourceETag, string destKey,
        IDictionary<string, string>? metadata, bool overwrite, CancellationToken ct);
    /// <summary>Quick check that the location is reachable with the configured permissions.</summary>
    Task<string> TestAsync(bool needWrite, CancellationToken ct);
}

public sealed class DestinationExistsException(string key) : Exception($"Destination already exists: {key}");
public sealed class SourceChangedException(string key) : Exception($"Source changed since scan (ETag mismatch): {key}");

public static class Hashing
{
    public static async Task<string> HashAsync(IDocumentStore store, string key, string method, CancellationToken ct)
    {
        if (method.Equals("Md5", StringComparison.OrdinalIgnoreCase))
        {
            var p = await store.GetPropsAsync(key, ct);
            if (!string.IsNullOrEmpty(p?.ContentMd5)) return p!.ContentMd5!;
        }
        await using var s = await store.OpenReadAsync(key, ct);
        using HashAlgorithm h = method.Equals("Md5", StringComparison.OrdinalIgnoreCase) ? MD5.Create() : SHA256.Create();
        var bytes = await h.ComputeHashAsync(s, ct);
        return method.Equals("Md5", StringComparison.OrdinalIgnoreCase) ? Convert.ToBase64String(bytes) : Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
