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
        using var h = Create(method);
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int n;
            while ((n = await s.ReadAsync(buf.AsMemory(0, BufferSize), ct)) > 0) h.AppendData(buf, 0, n);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
        return Format(method, h.GetHashAndReset());
    }

    /// <summary>Read size for hashing and copying: large reads are much faster on slow or external drives than 4 KB ones.</summary>
    public const int BufferSize = 1 << 20;

    public static IncrementalHash Create(string method) =>
        IncrementalHash.CreateHash(method.Equals("Md5", StringComparison.OrdinalIgnoreCase) ? HashAlgorithmName.MD5 : HashAlgorithmName.SHA256);

    public static string Format(string method, byte[] bytes) =>
        method.Equals("Md5", StringComparison.OrdinalIgnoreCase) ? Convert.ToBase64String(bytes) : Convert.ToHexString(bytes).ToLowerInvariant();
}

/// <summary>Read-only stream that hashes everything read through it (used to hash the source while copying it).</summary>
public sealed class HashingReadStream(Stream inner, string method) : Stream
{
    private readonly IncrementalHash _hash = Hashing.Create(method);
    private string? _result;
    public string Result() => _result ??= Hashing.Format(method, _hash.GetHashAndReset());
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) { int n = inner.Read(buffer, offset, count); if (n > 0) _hash.AppendData(buffer, offset, n); return n; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int n = await inner.ReadAsync(buffer, ct);
        if (n > 0) _hash.AppendData(buffer.Span[..n]);
        return n;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
}
