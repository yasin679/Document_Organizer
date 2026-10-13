using DocumentOrganizer.Domain;

namespace DocumentOrganizer.Storage;

public sealed class OrganizerOptions
{
    public string DataPath { get; set; } = "data";
    public int SessionTimeoutMinutes { get; set; } = 30;
    /// <summary>Local folders the application may read from or write to. Empty = any folder (not recommended outside a laptop).</summary>
    public List<string> AllowedLocalRoots { get; set; } = new();
    public Dictionary<string, StorageConnectionOptions> StorageConnections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool EnableDemoData { get; set; } = true;
    /// <summary>Set to true when the app runs behind a reverse proxy or load balancer that terminates HTTPS (honours X-Forwarded-For / -Proto).</summary>
    public bool BehindReverseProxy { get; set; }
}

/// <summary>Creates document stores for job locations. Cloud accounts are created once per named connection and reused.</summary>
public sealed class StorageFactory
{
    private readonly OrganizerOptions _opt;
    private readonly Dictionary<string, object> _accounts = new(StringComparer.OrdinalIgnoreCase);

    public StorageFactory(OrganizerOptions opt) { _opt = opt; }

    public IEnumerable<object> DescribeConnections() =>
        _opt.StorageConnections.Select(kv =>
        {
            string provider, auth, endpoint;
            try
            {
                provider = kv.Value.ResolveProvider();
                switch (Account(kv.Key))
                {
                    case BlobAccount b: auth = b.AuthDescription; endpoint = b.Endpoint.ToString(); break;
                    case S3Account s: auth = s.AuthDescription; endpoint = s.EndpointDisplay; break;
                    default: auth = ""; endpoint = ""; break;
                }
            }
            catch (Exception ex) { provider = SafeProvider(kv.Value); auth = "Invalid: " + ex.Message; endpoint = ""; }
            return (object)new { name = kv.Key, provider, auth, endpoint, description = kv.Value.Description, demo = kv.Value.UseForDemo };
        });

    private static string SafeProvider(StorageConnectionOptions o) { try { return o.ResolveProvider(); } catch { return ""; } }

    public object Account(string name)
    {
        lock (_accounts)
        {
            if (_accounts.TryGetValue(name, out var a)) return a;
            if (!_opt.StorageConnections.TryGetValue(name, out var o)) throw new ArgumentException($"Storage connection '{name}' is not configured.");
            a = o.ResolveProvider() == StorageProviders.S3 ? new S3Account(name, o) : new BlobAccount(name, o);
            _accounts[name] = a;
            return a;
        }
    }

    public IDocumentStore Create(Location loc)
    {
        if (loc.Type.Equals(StorageProviders.Local, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(loc.Path)) throw new ArgumentException("Folder path is required.");
            var full = Path.GetFullPath(loc.Path);
            if (_opt.AllowedLocalRoots.Count > 0 && !_opt.AllowedLocalRoots.Any(r => IsUnder(full, Path.GetFullPath(r))))
                throw new UnauthorizedAccessException($"Folder '{full}' is outside the allowed local roots set by the administrator.");
            return new LocalStore(full);
        }
        if (loc.Type.Equals(StorageProviders.AzureBlob, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(loc.Connection)) throw new ArgumentException("Storage connection is required.");
            return Account(loc.Connection) is BlobAccount b
                ? new AzureBlobStore(b, loc.Container ?? "", loc.Prefix)
                : throw new ArgumentException($"Connection '{loc.Connection}' is not an Azure Blob Storage connection.");
        }
        if (loc.Type.Equals(StorageProviders.S3, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(loc.Connection)) throw new ArgumentException("Storage connection is required.");
            return Account(loc.Connection) is S3Account s
                ? new S3Store(s, loc.Container ?? "", loc.Prefix)
                : throw new ArgumentException($"Connection '{loc.Connection}' is not an Amazon S3 connection.");
        }
        throw new ArgumentException($"Unknown location type '{loc.Type}'. Use Local, AzureBlob or S3.");
    }

    private static bool IsUnder(string path, string root)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(root, cmp);
    }
}
