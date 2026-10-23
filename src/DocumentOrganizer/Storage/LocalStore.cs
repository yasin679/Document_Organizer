using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DocumentOrganizer.Domain;

namespace DocumentOrganizer.Storage;

public sealed class LocalStore : IDocumentStore
{
    private readonly string _root;
    public LocalStore(string root) { _root = Path.GetFullPath(root); }
    public string Kind => StorageProviders.Local;
    public string Root => _root;
    public string Describe => _root;

    private string Full(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Path escapes the configured root.");
        return full;
    }

    public async IAsyncEnumerable<SourceFile> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException($"Source folder not found: {_root}");
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var f in Directory.EnumerateFiles(_root, "*", opts))
        {
            ct.ThrowIfCancellationRequested();
            FileInfo fi;
            try { fi = new FileInfo(f); } catch { continue; }
            var rel = Path.GetRelativePath(_root, f).Replace(Path.DirectorySeparatorChar, '/');
            yield return new SourceFile { Key = rel, Size = fi.Length, ETag = fi.LastWriteTimeUtc.Ticks.ToString("x") + "-" + fi.Length, LastModified = fi.LastWriteTimeUtc };
        }
        await Task.CompletedTask;
    }

    public Task<FileProps?> GetPropsAsync(string key, CancellationToken ct)
    {
        var fi = new FileInfo(Full(key));
        return Task.FromResult(fi.Exists
            ? new FileProps(fi.Length, fi.LastWriteTimeUtc.Ticks.ToString("x") + "-" + fi.Length, null, new Dictionary<string, string>())
            : null);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(Full(key), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan));

    public async Task WriteAsync(string key, Stream content, IDictionary<string, string>? metadata, bool overwrite, CancellationToken ct)
    {
        var full = Full(key);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!overwrite && File.Exists(full)) throw new DestinationExistsException(key);
        var tmp = full + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, true))
                await content.CopyToAsync(fs, Hashing.BufferSize, ct);
            PlaceAtomically(tmp, full, key, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(full)) { throw new DestinationExistsException(key); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // One lock per destination path (striped), so two jobs writing the same file cannot both pass the "does it exist" check.
    private static readonly object[] PathLocks = Enumerable.Range(0, 1024).Select(_ => new object()).ToArray();
    private static readonly StringComparer PathCompare = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Moves the finished temporary file to its final name. Without overwrite, an existing file is never replaced.</summary>
    private static void PlaceAtomically(string tmp, string full, string key, bool overwrite)
    {
        lock (PathLocks[(PathCompare.GetHashCode(full) & int.MaxValue) % PathLocks.Length])
        {
            if (!overwrite && File.Exists(full)) throw new DestinationExistsException(key);
            File.Move(tmp, full, overwrite);
        }
    }

    /// <summary>
    /// Removes half-written copies (".partial-xxxxxxxx") of this destination file, and of its "_dupN" variants,
    /// left behind when the application stopped in the middle of copying it.
    /// </summary>
    public int DeletePartials(string key)
    {
        var full = Full(key);
        var dir = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(dir)) return 0;
        var name = Path.GetFileName(full);
        var stem = Path.GetFileNameWithoutExtension(full); var ext = Path.GetExtension(full);
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, name + ".partial-*").Concat(Directory.EnumerateFiles(dir, stem + "_dup*" + ext + ".partial-*")))
            try { File.Delete(f); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return n;
    }

    public async Task<bool> TryServerCopyAsync(IDocumentStore source, string sourceKey, string? sourceETag, string destKey,
        IDictionary<string, string>? metadata, bool overwrite, CancellationToken ct)
    {
        if (source is not LocalStore ls) return false;
        if (sourceETag != null)
        {
            var p = await ls.GetPropsAsync(sourceKey, ct);
            if (p == null) throw new FileNotFoundException("Source file no longer exists.", sourceKey);
            if (p.ETag != sourceETag) throw new SourceChangedException(sourceKey);
        }
        var dest = Full(destKey);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (!overwrite && File.Exists(dest)) throw new DestinationExistsException(destKey);
        // Copy to a temporary name first, then rename: a crash never leaves a half-written file under the final name.
        var tmp = dest + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.Copy(ls.Full(sourceKey), tmp, false);
            PlaceAtomically(tmp, dest, destKey, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(dest)) { throw new DestinationExistsException(destKey); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        await Task.CompletedTask;
        return true;
    }

    public Task<string> TestAsync(bool needWrite, CancellationToken ct)
    {
        if (needWrite)
        {
            Directory.CreateDirectory(_root);
            var probe = Path.Combine(_root, ".docorg-write-test-" + RandomNumberGenerator.GetHexString(6));
            File.WriteAllText(probe, "ok"); File.Delete(probe);
            return Task.FromResult($"Write access confirmed for {_root}.");
        }
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException($"Folder not found: {_root}");
        int n = Directory.EnumerateFileSystemEntries(_root).Take(1000).Count();
        return Task.FromResult($"Read access confirmed for {_root} ({(n >= 1000 ? "1,000+" : n.ToString())} entries at the top level).");
    }
}
