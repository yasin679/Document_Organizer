using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using DocumentOrganizer.Domain;

namespace DocumentOrganizer.Storage;

/// <summary>Authenticated HTTP access to one Azure storage account's Blob service (REST API).</summary>
public sealed class BlobAccount
{
    public const string ApiVersion = "2021-12-02";
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxConnectionsPerServer = 64 }) { Timeout = TimeSpan.FromMinutes(30) };

    public string Name { get; }
    public Uri Endpoint { get; }            // e.g. https://acct.blob.core.windows.net/  or http://127.0.0.1:10000/devstoreaccount1/
    public string AccountName { get; }
    public string AuthMode { get; }          // SharedKey | Sas | Token
    private readonly byte[]? _key;
    private readonly string? _sas;
    private readonly ITokenSource? _tokens;

    public BlobAccount(string name, StorageConnectionOptions o)
    {
        Name = name;
        if (!string.IsNullOrWhiteSpace(o.ConnectionString))
        {
            var cs = ParseConnectionString(o.ConnectionString);
            if (cs.TryGetValue("UseDevelopmentStorage", out var dev) && dev.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                cs["AccountName"] = "devstoreaccount1";
                cs["AccountKey"] = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
                cs["BlobEndpoint"] = cs.TryGetValue("DevelopmentStorageProxyUri", out var px) ? px.TrimEnd('/') + ":10000/devstoreaccount1" : "http://127.0.0.1:10000/devstoreaccount1";
            }
            cs.TryGetValue("AccountName", out var acct);
            string endpoint = cs.TryGetValue("BlobEndpoint", out var be) ? be
                : $"{(cs.TryGetValue("DefaultEndpointsProtocol", out var proto) ? proto : "https")}://{acct}.blob.{(cs.TryGetValue("EndpointSuffix", out var suf) ? suf : "core.windows.net")}";
            Endpoint = new Uri(endpoint.TrimEnd('/') + "/");
            AccountName = acct ?? Endpoint.Host.Split('.')[0];
            if (cs.TryGetValue("AccountKey", out var key)) { _key = Convert.FromBase64String(key); AuthMode = "SharedKey"; }
            else if (cs.TryGetValue("SharedAccessSignature", out var sas)) { _sas = sas.TrimStart('?'); AuthMode = "Sas"; }
            else throw new InvalidOperationException($"Connection '{name}': connection string has neither AccountKey nor SharedAccessSignature.");
            return;
        }
        if (string.IsNullOrWhiteSpace(o.AccountUrl)) throw new InvalidOperationException($"Connection '{name}': set ConnectionString or AccountUrl.");
        Endpoint = new Uri(o.AccountUrl.TrimEnd('/') + "/");
        AccountName = Endpoint.Host.Split('.')[0];
        var auth = o.Auth ?? (o.SasToken != null ? "Sas" : "ManagedIdentity");
        switch (auth.ToLowerInvariant())
        {
            case "sas":
                _sas = (o.SasToken ?? throw new InvalidOperationException($"Connection '{name}': SasToken missing.")).TrimStart('?');
                AuthMode = "Sas"; break;
            case "managedidentity":
                _tokens = new ManagedIdentityTokenSource(o.ManagedIdentityClientId); AuthMode = "Token"; break;
            case "azurecli":
                _tokens = new AzureCliTokenSource(); AuthMode = "Token"; break;
            default: throw new InvalidOperationException($"Connection '{name}': unknown Auth '{auth}'.");
        }
    }

    public string AuthDescription => AuthMode switch
    {
        "SharedKey" => "Account key (connection string)",
        "Sas" => "SAS token",
        _ => _tokens is AzureCliTokenSource ? "Azure CLI sign-in (Microsoft Entra ID)" : "Managed identity (Microsoft Entra ID)"
    };

    private static Dictionary<string, string> ParseConnectionString(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int i = part.IndexOf('=');
            if (i > 0) d[part[..i].Trim()] = part[(i + 1)..].Trim();
        }
        return d;
    }

    public static string EncodePath(string path) => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    public Uri BlobUri(string container, string blobName, string? query = null)
    {
        var u = Endpoint + Uri.EscapeDataString(container) + (blobName.Length > 0 ? "/" + EncodePath(blobName) : "");
        return new Uri(AppendQuery(u, query));
    }

    private string AppendQuery(string url, string? query)
    {
        var q = query ?? "";
        if (_sas != null) q = q.Length > 0 ? q + "&" + _sas : _sas;
        return q.Length > 0 ? url + "?" + q : url;
    }

    /// <summary>Source URL usable in x-ms-copy-source (includes the SAS when the account uses SAS).</summary>
    public string CopySourceUrl(string container, string blobName) => BlobUri(container, blobName).ToString();

    public bool SameAccount(BlobAccount other) => Endpoint == other.Endpoint;

    /// <summary>Sends a request with authentication and retries throttling / transient errors with exponential back-off.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, Action<HttpRequestMessage>? configure, Func<HttpContent?>? body,
        CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, params HttpStatusCode[] okCodes)
    {
        const int maxAttempts = 7;
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, uri);
            req.Headers.TryAddWithoutValidation("x-ms-version", ApiVersion);
            req.Headers.TryAddWithoutValidation("x-ms-date", DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture));
            req.Headers.TryAddWithoutValidation("x-ms-client-request-id", Guid.NewGuid().ToString());
            req.Content = body?.Invoke();
            configure?.Invoke(req);
            if (req.Content != null && req.Content.Headers.ContentLength == null) req.Content.Headers.ContentLength = 0;
            if (method == HttpMethod.Put && req.Content == null) { req.Content = new ByteArrayContent(Array.Empty<byte>()); }
            await AuthorizeAsync(req, ct);
            HttpResponseMessage? resp = null;
            Exception? error = null;
            try { resp = await Http.SendAsync(req, completion, ct); }
            catch (HttpRequestException ex) { error = ex; }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { error = ex; }

            if (resp != null && (okCodes.Length == 0 ? resp.IsSuccessStatusCode : okCodes.Contains(resp.StatusCode))) return resp;
            bool transient = error != null || resp!.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;
            if (!transient || attempt >= maxAttempts)
            {
                if (error != null) throw new BlobRequestException(0, "NetworkError", error.Message);
                var text = await resp!.Content.ReadAsStringAsync(ct);
                var code = resp.Headers.TryGetValues("x-ms-error-code", out var v) ? v.First() : ExtractXml(text, "Code") ?? resp.StatusCode.ToString();
                var msg = ExtractXml(text, "Message")?.Split('\n')[0] ?? resp.ReasonPhrase ?? "";
                var status = (int)resp.StatusCode;
                resp.Dispose();
                throw new BlobRequestException(status, code, msg);
            }
            TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(30000, 400 * Math.Pow(2, attempt - 1)) + Random.Shared.Next(0, 250));
            if (resp?.Headers.RetryAfter?.Delta is TimeSpan ra) delay = ra;
            resp?.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    private static string? ExtractXml(string text, string el)
    {
        int a = text.IndexOf("<" + el + ">", StringComparison.Ordinal);
        if (a < 0) return null;
        int b = text.IndexOf("</" + el + ">", a, StringComparison.Ordinal);
        return b > a ? text[(a + el.Length + 2)..b] : null;
    }

    private async Task AuthorizeAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (AuthMode == "Token")
        {
            var token = await _tokens!.GetTokenAsync(ct);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }
        else if (AuthMode == "SharedKey")
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"SharedKey {AccountName}:{SignSharedKey(req)}");
        }
    }

    private string SignSharedKey(HttpRequestMessage req)
    {
        string H(string name)
        {
            if (req.Headers.TryGetValues(name, out var v)) return string.Join(",", v);
            if (req.Content != null && req.Content.Headers.TryGetValues(name, out var cv)) return string.Join(",", cv);
            return "";
        }
        var len = req.Content?.Headers.ContentLength;
        var lenStr = len is > 0 ? len.Value.ToString(CultureInfo.InvariantCulture) : "";
        string contentMd5 = req.Content?.Headers.ContentMD5 is byte[] md5 ? Convert.ToBase64String(md5) : "";
        var sb = new StringBuilder();
        sb.Append(req.Method.Method).Append('\n')
          .Append(H("Content-Encoding")).Append('\n')
          .Append(H("Content-Language")).Append('\n')
          .Append(lenStr).Append('\n')
          .Append(contentMd5).Append('\n')
          .Append(req.Content?.Headers.ContentType?.ToString() ?? "").Append('\n')
          .Append("").Append('\n') // Date (x-ms-date is used)
          .Append(H("If-Modified-Since")).Append('\n')
          .Append(H("If-Match")).Append('\n')
          .Append(H("If-None-Match")).Append('\n')
          .Append(H("If-Unmodified-Since")).Append('\n')
          .Append(H("Range")).Append('\n');
        var msHeaders = req.Headers.Where(h => h.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
            .Select(h => (k: h.Key.ToLowerInvariant(), v: string.Join(",", h.Value).Trim()))
            .OrderBy(h => h.k, StringComparer.Ordinal);
        foreach (var (k, v) in msHeaders) sb.Append(k).Append(':').Append(v).Append('\n');
        sb.Append('/').Append(AccountName).Append(req.RequestUri!.AbsolutePath);
        var query = req.RequestUri.Query.TrimStart('?');
        if (query.Length > 0)
        {
            var pairs = query.Split('&').Select(p => { int i = p.IndexOf('='); return i < 0 ? (k: Uri.UnescapeDataString(p).ToLowerInvariant(), v: "") : (k: Uri.UnescapeDataString(p[..i]).ToLowerInvariant(), v: Uri.UnescapeDataString(p[(i + 1)..])); })
                .GroupBy(p => p.k).OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var g in pairs) sb.Append('\n').Append(g.Key).Append(':').Append(string.Join(",", g.Select(x => x.v).OrderBy(x => x, StringComparer.Ordinal)));
        }
        using var hmac = new HMACSHA256(_key!);
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

public sealed class BlobRequestException(int status, string code, string message)
    : StorageRequestException(status, code, message, "Azure Storage");

public interface ITokenSource { Task<string> GetTokenAsync(CancellationToken ct); }

/// <summary>Managed identity on Azure App Service / Functions (IDENTITY_ENDPOINT) or Azure VMs (IMDS).</summary>
public sealed class ManagedIdentityTokenSource(string? clientId) : ITokenSource
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private string? _token; private DateTimeOffset _expires;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token != null && _expires > DateTimeOffset.UtcNow.AddMinutes(5)) return _token;
        await _lock.WaitAsync(ct);
        try
        {
            if (_token != null && _expires > DateTimeOffset.UtcNow.AddMinutes(5)) return _token;
            const string resource = "https://storage.azure.com/";
            HttpRequestMessage req;
            var ep = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
            var hdr = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
            if (!string.IsNullOrEmpty(ep) && !string.IsNullOrEmpty(hdr))
            {
                req = new HttpRequestMessage(HttpMethod.Get, $"{ep}?resource={Uri.EscapeDataString(resource)}&api-version=2019-08-01{(clientId != null ? "&client_id=" + clientId : "")}");
                req.Headers.Add("X-IDENTITY-HEADER", hdr);
            }
            else
            {
                req = new HttpRequestMessage(HttpMethod.Get, $"http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource={Uri.EscapeDataString(resource)}{(clientId != null ? "&client_id=" + clientId : "")}");
                req.Headers.Add("Metadata", "true");
            }
            using var resp = await Http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Managed identity token request failed ({(int)resp.StatusCode}). Is a managed identity assigned to this host?");
            using var doc = JsonDocument.Parse(text);
            _token = doc.RootElement.GetProperty("access_token").GetString();
            _expires = ParseExpiry(doc.RootElement);
            return _token!;
        }
        catch (HttpRequestException) { throw new InvalidOperationException("No managed identity endpoint is reachable from this machine. Use Auth=AzureCli on a laptop, or run on Azure."); }
        finally { _lock.Release(); }
    }

    internal static DateTimeOffset ParseExpiry(JsonElement root)
    {
        if (root.TryGetProperty("expires_on", out var eo))
        {
            var s = eo.ValueKind == JsonValueKind.Number ? eo.GetRawText() : eo.GetString();
            if (long.TryParse(s, out var epoch)) return DateTimeOffset.FromUnixTimeSeconds(epoch);
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)) return d;
        }
        if (root.TryGetProperty("expires_in", out var ei) && long.TryParse(ei.ValueKind == JsonValueKind.Number ? ei.GetRawText() : ei.GetString(), out var secs))
            return DateTimeOffset.UtcNow.AddSeconds(secs);
        return DateTimeOffset.UtcNow.AddMinutes(30);
    }
}

/// <summary>Uses the signed-in Azure CLI account (az login) — convenient on a laptop.</summary>
public sealed class AzureCliTokenSource : ITokenSource
{
    private string? _token; private DateTimeOffset _expires;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token != null && _expires > DateTimeOffset.UtcNow.AddMinutes(5)) return _token;
        await _lock.WaitAsync(ct);
        try
        {
            if (_token != null && _expires > DateTimeOffset.UtcNow.AddMinutes(5)) return _token;
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c az account get-access-token --resource https://storage.azure.com/ --output json")
                : new ProcessStartInfo("az", "account get-access-token --resource https://storage.azure.com/ --output json");
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.UseShellExecute = false; psi.CreateNoWindow = true;
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Azure CLI.");
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            var err = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new InvalidOperationException("Azure CLI could not get a token. Run 'az login' first. " + err.Split('\n')[0]);
            using var doc = JsonDocument.Parse(output);
            _token = doc.RootElement.GetProperty("accessToken").GetString();
            _expires = ManagedIdentityTokenSource.ParseExpiry(doc.RootElement);
            if (_expires == default && doc.RootElement.TryGetProperty("expiresOn", out var eo) && DateTimeOffset.TryParse(eo.GetString(), out var d)) _expires = d;
            return _token!;
        }
        finally { _lock.Release(); }
    }
}

/// <summary>A container (and optional prefix) in an Azure storage account.</summary>
public sealed class AzureBlobStore : IDocumentStore
{
    public BlobAccount Account { get; }
    public string Container { get; }
    public string Prefix { get; }   // "" or ends with '/'

    public AzureBlobStore(BlobAccount account, string container, string? prefix)
    {
        Account = account;
        Container = (container ?? "").Trim().Trim('/');
        if (Container.Length == 0) throw new ArgumentException("Container is required.");
        var p = (prefix ?? "").Trim().TrimStart('/');
        Prefix = p.Length == 0 || p.EndsWith('/') ? p : p + "/";
    }

    public string Kind => StorageProviders.AzureBlob;
    public string Describe => $"{Account.Name}/{Container}/{Prefix}";
    private string Name(string key) => Prefix + key;
    private Uri ContainerUri(string query) => Account.BlobUri(Container, "", query);

    public async IAsyncEnumerable<SourceFile> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        string? marker = null;
        do
        {
            var q = "restype=container&comp=list&maxresults=5000" + (Prefix.Length > 0 ? "&prefix=" + Uri.EscapeDataString(Prefix) : "") + (marker != null ? "&marker=" + Uri.EscapeDataString(marker) : "");
            using var resp = await Account.SendAsync(HttpMethod.Get, ContainerUri(q), null, null, ct);
            var xml = await resp.Content.ReadAsStringAsync(ct);
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml);
            var list = new List<SourceFile>();
            foreach (XmlElement b in doc.SelectNodes("/EnumerationResults/Blobs/Blob")!)
            {
                var name = b["Name"]?.InnerText ?? "";
                if (name.EndsWith('/')) continue; // folder placeholder
                var props = b["Properties"];
                if (props?["ResourceType"]?.InnerText == "directory") continue; // HNS directory
                long.TryParse(props?["Content-Length"]?.InnerText, out var size);
                list.Add(new SourceFile
                {
                    Key = name[Prefix.Length..],
                    Size = size,
                    ETag = QuoteETag(props?["Etag"]?.InnerText),
                    ContentMd5 = string.IsNullOrEmpty(props?["Content-MD5"]?.InnerText) ? null : props!["Content-MD5"]!.InnerText,
                    AccessTier = props?["AccessTier"]?.InnerText,
                    LastModified = DateTimeOffset.TryParse(props?["Last-Modified"]?.InnerText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lm) ? lm : null
                });
            }
            foreach (var f in list) yield return f;
            marker = doc.SelectSingleNode("/EnumerationResults/NextMarker")?.InnerText;
            if (string.IsNullOrEmpty(marker)) marker = null;
        } while (marker != null);
    }

    public async Task<FileProps?> GetPropsAsync(string key, CancellationToken ct)
    {
        try
        {
            using var resp = await Account.SendAsync(HttpMethod.Head, Account.BlobUri(Container, Name(key)), null, null, ct);
            return ToProps(resp);
        }
        catch (BlobRequestException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>List Blobs returns ETags unquoted; HEAD returns them quoted. Use the quoted form everywhere.</summary>
    private static string? QuoteETag(string? e) => string.IsNullOrEmpty(e) ? null : e.StartsWith('"') ? e : $"\"{e}\"";

    private static FileProps ToProps(HttpResponseMessage resp)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers) if (h.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase)) meta[h.Key[10..]] = h.Value.First();
        var md5 = resp.Content.Headers.ContentMD5 is byte[] m ? Convert.ToBase64String(m)
            : resp.Headers.TryGetValues("x-ms-blob-content-md5", out var bm) ? bm.First() : null;
        return new FileProps(resp.Content.Headers.ContentLength ?? 0, resp.Headers.ETag?.Tag, md5, meta);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var resp = await Account.SendAsync(HttpMethod.Get, Account.BlobUri(Container, Name(key)), null, null, ct, HttpCompletionOption.ResponseHeadersRead);
        var s = await resp.Content.ReadAsStreamAsync(ct);
        return new OwnedStream(s, resp);
    }

    private static void AddMeta(HttpRequestMessage req, IDictionary<string, string>? metadata)
    {
        if (metadata == null) return;
        foreach (var (k, v) in metadata)
        {
            var name = MetaName(k);
            if (name.Length > 0) req.Headers.TryAddWithoutValidation("x-ms-meta-" + name, StreamUtil.EncodeMetaValue(v));
        }
    }

    /// <summary>Azure metadata names must be valid C# identifiers (metadata copied from S3 may contain '-' or '.').</summary>
    private static string MetaName(string k)
    {
        var n = new string(k.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return n.Length > 0 && char.IsAsciiDigit(n[0]) ? "_" + n : n;
    }

    public async Task WriteAsync(string key, Stream content, IDictionary<string, string>? metadata, bool overwrite, CancellationToken ct)
    {
        const int block = 8 * 1024 * 1024;
        var uri = Account.BlobUri(Container, Name(key));
        using var md5 = MD5.Create();
        var buffer = new byte[block];
        int first = await StreamUtil.ReadFull(content, buffer, ct);
        if (first < block)
        {
            var data = buffer.AsSpan(0, first).ToArray();
            var hash = MD5.HashData(data);
            try
            {
                using var _ = await Account.SendAsync(HttpMethod.Put, uri, r =>
                {
                    r.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
                    if (!overwrite) r.Headers.TryAddWithoutValidation("If-None-Match", "*");
                    AddMeta(r, metadata);
                }, () => { var c = new ByteArrayContent(data); c.Headers.ContentMD5 = hash; c.Headers.ContentType = new("application/octet-stream"); return c; }, ct);
            }
            catch (BlobRequestException ex) when (ex.Status is 409 or 412 && !overwrite) { throw new DestinationExistsException(key); }
            return;
        }
        if (!overwrite && await GetPropsAsync(key, ct) != null) throw new DestinationExistsException(key);
        var ids = new List<string>();
        int n = first;
        while (n > 0)
        {
            md5.TransformBlock(buffer, 0, n, null, 0);
            var id = Convert.ToBase64String(Encoding.ASCII.GetBytes(ids.Count.ToString("d8")));
            var chunk = buffer.AsSpan(0, n).ToArray();
            using (await Account.SendAsync(HttpMethod.Put, Account.BlobUri(Container, Name(key), "comp=block&blockid=" + Uri.EscapeDataString(id)), null,
                       () => new ByteArrayContent(chunk), ct)) { }
            ids.Add(id);
            n = await StreamUtil.ReadFull(content, buffer, ct);
        }
        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var listXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>" + string.Concat(ids.Select(i => $"<Latest>{i}</Latest>")) + "</BlockList>";
        try
        {
            using var _ = await Account.SendAsync(HttpMethod.Put, Account.BlobUri(Container, Name(key), "comp=blocklist"), r =>
            {
                r.Headers.TryAddWithoutValidation("x-ms-blob-content-md5", Convert.ToBase64String(md5.Hash!));
                if (!overwrite) r.Headers.TryAddWithoutValidation("If-None-Match", "*");
                AddMeta(r, metadata);
            }, () => new StringContent(listXml, Encoding.UTF8, "application/xml"), ct);
        }
        catch (BlobRequestException ex) when (ex.Status is 409 or 412 && !overwrite) { throw new DestinationExistsException(key); }
    }

    public async Task<bool> TryServerCopyAsync(IDocumentStore source, string sourceKey, string? sourceETag, string destKey,
        IDictionary<string, string>? metadata, bool overwrite, CancellationToken ct)
    {
        if (source is not AzureBlobStore src || !src.Account.SameAccount(Account)) return false;
        var destUri = Account.BlobUri(Container, Name(destKey));
        HttpResponseMessage resp;
        try
        {
            resp = await Account.SendAsync(HttpMethod.Put, destUri, r =>
            {
                r.Headers.TryAddWithoutValidation("x-ms-copy-source", src.Account.CopySourceUrl(src.Container, src.Prefix + sourceKey));
                if (sourceETag != null) r.Headers.TryAddWithoutValidation("x-ms-source-if-match", sourceETag);
                if (!overwrite) r.Headers.TryAddWithoutValidation("If-None-Match", "*");
                AddMeta(r, metadata);
            }, null, ct, HttpCompletionOption.ResponseContentRead, HttpStatusCode.Accepted, HttpStatusCode.Created);
        }
        catch (BlobRequestException ex) when (ex.Code is "BlobAlreadyExists" || (ex.Status == 412 && ex.Code != "SourceConditionNotMet" && !overwrite) || (ex.Status == 409 && !overwrite && ex.Code != "PendingCopyOperation"))
        { throw new DestinationExistsException(destKey); }
        catch (BlobRequestException ex) when (ex.Code is "SourceConditionNotMet") { throw new SourceChangedException(sourceKey); }
        catch (BlobRequestException ex) when (ex.Code is "CannotVerifyCopySource" || ex.Status == 403 && ex.Code.Contains("Copy", StringComparison.OrdinalIgnoreCase))
        { return false; }
        using (resp)
        {
            var status = resp.Headers.TryGetValues("x-ms-copy-status", out var cs) ? cs.First() : "success";
            var deadline = DateTime.UtcNow.AddMinutes(60);
            while (status == "pending")
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Server-side copy did not finish within 60 minutes.");
                await Task.Delay(500, ct);
                using var head = await Account.SendAsync(HttpMethod.Head, destUri, null, null, ct);
                status = head.Headers.TryGetValues("x-ms-copy-status", out var s2) ? s2.First() : "success";
                if (status is "failed" or "aborted")
                {
                    var desc = head.Headers.TryGetValues("x-ms-copy-status-description", out var d) ? d.First() : "";
                    throw new InvalidOperationException($"Server-side copy {status}: {desc}");
                }
            }
            if (status is "failed" or "aborted") throw new InvalidOperationException($"Server-side copy {status}.");
        }
        return true;
    }

    public async Task<string> TestAsync(bool needWrite, CancellationToken ct)
    {
        if (needWrite)
        {
            // Create the container if it does not exist (needs Contributor), then write and delete a probe blob.
            try { using var _ = await Account.SendAsync(HttpMethod.Put, ContainerUri("restype=container"), null, null, ct, HttpCompletionOption.ResponseContentRead, HttpStatusCode.Created); }
            catch (BlobRequestException ex) when (ex.Code is "ContainerAlreadyExists" || ex.Status == 409) { }
            catch (BlobRequestException ex) when (ex.Status == 403) { /* may not have container-create rights; try blob write anyway */ }
            var probe = Prefix + ".docorg-write-test-" + RandomNumberGenerator.GetHexString(6);
            using (await Account.SendAsync(HttpMethod.Put, Account.BlobUri(Container, probe), r => r.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob"),
                       () => new ByteArrayContent(Encoding.ASCII.GetBytes("ok")), ct)) { }
            using (await Account.SendAsync(HttpMethod.Delete, Account.BlobUri(Container, probe), null, null, ct)) { }
            return $"Write access confirmed for {Describe} ({Account.AuthDescription}).";
        }
        using var resp = await Account.SendAsync(HttpMethod.Get, ContainerUri("restype=container&comp=list&maxresults=1" + (Prefix.Length > 0 ? "&prefix=" + Uri.EscapeDataString(Prefix) : "")), null, null, ct);
        var xml = await resp.Content.ReadAsStringAsync(ct);
        bool any = xml.Contains("<Blob>", StringComparison.Ordinal);
        return $"List and read access confirmed for {Describe} ({Account.AuthDescription}).{(any ? "" : " No blobs found under this prefix yet.")}";
    }
}
