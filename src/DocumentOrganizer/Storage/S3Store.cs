using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DocumentOrganizer.Domain;

namespace DocumentOrganizer.Storage;

public sealed class S3RequestException(int status, string code, string message)
    : StorageRequestException(status, code, message, "Amazon S3");

/// <summary>
/// Authenticated HTTP access to Amazon S3 or an S3-compatible service (REST API, Signature Version 4).
/// No AWS SDK is needed.
/// </summary>
public sealed class S3Account
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxConnectionsPerServer = 64, AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromMinutes(30) };

    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public string Name { get; }
    public string Region { get; }
    public Uri? ServiceUrl { get; }
    public bool PathStyle { get; }
    public IAwsCredentialSource Credentials { get; }

    public S3Account(string name, StorageConnectionOptions o)
    {
        Name = name;
        Region = FirstNonEmpty(o.Region, Environment.GetEnvironmentVariable("AWS_REGION"), Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")) ?? "us-east-1";
        if (!string.IsNullOrWhiteSpace(o.ServiceUrl))
        {
            if (!Uri.TryCreate(o.ServiceUrl.TrimEnd('/') + "/", UriKind.Absolute, out var su) || su.Scheme is not ("http" or "https"))
                throw new InvalidOperationException($"Connection '{name}': ServiceUrl must be an http(s) URL.");
            ServiceUrl = su;
            PathStyle = o.ForcePathStyle ?? true;
        }
        else PathStyle = o.ForcePathStyle ?? false;

        var auth = o.Auth ?? (string.IsNullOrWhiteSpace(o.AccessKeyId) ? "Default" : "AccessKey");
        Credentials = auth.ToLowerInvariant() switch
        {
            "accesskey" => new StaticAwsCredentials(
                o.AccessKeyId ?? throw new InvalidOperationException($"Connection '{name}': AccessKeyId missing."),
                o.SecretAccessKey ?? throw new InvalidOperationException($"Connection '{name}': SecretAccessKey missing."),
                string.IsNullOrWhiteSpace(o.SessionToken) ? null : o.SessionToken),
            "default" => new DefaultAwsCredentials(Region, o.Profile),
            "environment" => new EnvironmentAwsCredentials(),
            "profile" => new ProfileAwsCredentials(o.Profile),
            "webidentity" => new WebIdentityAwsCredentials(Region),
            "container" => new ContainerAwsCredentials(),
            "instanceprofile" => new InstanceProfileAwsCredentials(),
            _ => throw new InvalidOperationException($"Connection '{name}': unknown Auth '{auth}' for S3.")
        };
    }

    private static string? FirstNonEmpty(params string?[] v) => v.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    public string AuthDescription => Credentials.Description;
    public string EndpointDisplay => ServiceUrl?.ToString().TrimEnd('/') ?? $"https://s3.{Region}.amazonaws.com";

    /// <summary>RFC 3986 encoding, as Signature Version 4 requires.</summary>
    public static string Enc(string s) => Uri.EscapeDataString(s);
    public static string EncPath(string key) => string.Join("/", key.Split('/').Select(Enc));

    private (Uri Uri, string Path, string Query) Build(string bucket, string key, IEnumerable<KeyValuePair<string, string>>? query)
    {
        string scheme, authority, basePath;
        if (ServiceUrl != null) { scheme = ServiceUrl.Scheme; authority = ServiceUrl.Authority; basePath = ServiceUrl.AbsolutePath.TrimEnd('/'); }
        else { scheme = "https"; authority = $"s3.{Region}.amazonaws.com"; basePath = ""; }
        // Bucket names with dots do not match the wildcard TLS certificate of virtual-hosted URLs.
        bool pathStyle = PathStyle || (scheme == "https" && bucket.Contains('.'));
        string path;
        if (pathStyle) path = basePath + "/" + Enc(bucket) + (key.Length > 0 ? "/" + EncPath(key) : "");
        else { authority = bucket + "." + authority; path = basePath + "/" + EncPath(key); }
        var q = string.Join("&", (query ?? Enumerable.Empty<KeyValuePair<string, string>>())
            .Select(kv => (k: Enc(kv.Key), v: Enc(kv.Value ?? "")))
            .OrderBy(p => p.k, StringComparer.Ordinal).ThenBy(p => p.v, StringComparer.Ordinal)
            .Select(p => p.k + "=" + p.v));
        var url = $"{scheme}://{authority}{path}{(q.Length > 0 ? "?" + q : "")}";
        // Keep the URL exactly as signed (no dot-segment removal or re-escaping).
        return (new Uri(url, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true }), path, q);
    }

    public static IEnumerable<KeyValuePair<string, string>> Q(params (string k, string? v)[] p) =>
        p.Where(x => x.v != null).Select(x => new KeyValuePair<string, string>(x.k, x.v!));

    /// <summary>Sends a signed request and retries throttling / transient errors with exponential back-off.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string bucket, string key, IEnumerable<KeyValuePair<string, string>>? query,
        Action<HttpRequestMessage>? configure, byte[]? body, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var (uri, path, cq) = Build(bucket, key, query?.ToList());
        var payloadHash = body is { Length: > 0 } ? Hex(SHA256.HashData(body)) : EmptySha256;
        const int maxAttempts = 7;
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, uri);
            if (body != null || method == HttpMethod.Put || method == HttpMethod.Post) req.Content = new ByteArrayContent(body ?? Array.Empty<byte>());
            configure?.Invoke(req);
            var creds = await Credentials.GetAsync(ct);
            Sign(req, path, cq, payloadHash, creds, DateTime.UtcNow);
            HttpResponseMessage? resp = null; Exception? error = null;
            try { resp = await Http.SendAsync(req, completion, ct); }
            catch (HttpRequestException ex) { error = ex; }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { error = ex; }

            if (resp != null && resp.IsSuccessStatusCode) return resp;
            bool transient = error != null || resp!.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;
            if (!transient || attempt >= maxAttempts)
            {
                if (error != null) throw new S3RequestException(0, "NetworkError", error.Message);
                var text = method == HttpMethod.Head ? "" : await resp!.Content.ReadAsStringAsync(ct);
                var status = (int)resp!.StatusCode;
                var code = XmlValue(text, "Code") ?? (status == 404 ? "NotFound" : resp.StatusCode.ToString());
                var msg = XmlValue(text, "Message") ?? resp.ReasonPhrase ?? "";
                if ((status is 301 or 307 or 400) && resp.Headers.TryGetValues("x-amz-bucket-region", out var br) && br.First() != Region)
                    msg = $"The bucket is in region '{br.First()}', but the connection uses '{Region}'. Set Region on the connection.";
                resp.Dispose();
                throw new S3RequestException(status, code, msg);
            }
            TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(30000, 400 * Math.Pow(2, attempt - 1)) + Random.Shared.Next(0, 250));
            if (resp?.Headers.RetryAfter?.Delta is TimeSpan ra) delay = ra;
            resp?.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    /// <summary>Signature Version 4 (header-based).</summary>
    private void Sign(HttpRequestMessage req, string canonicalPath, string canonicalQuery, string payloadHash, AwsCredentials c, DateTime now)
    {
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = amzDate[..8];
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (!string.IsNullOrEmpty(c.SessionToken)) req.Headers.TryAddWithoutValidation("x-amz-security-token", c.SessionToken);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = req.RequestUri!.Authority };
        static string Norm(IEnumerable<string> v) => Regex.Replace(string.Join(",", v).Trim(), @"\s+", " ");
        foreach (var h in req.Headers)
            if (h.Key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase)) headers[h.Key.ToLowerInvariant()] = Norm(h.Value);
        if (req.Content != null)
            foreach (var h in req.Content.Headers)
            {
                var k = h.Key.ToLowerInvariant();
                if (k is "content-md5" or "content-type" || k.StartsWith("x-amz-")) headers[k] = Norm(h.Value);
            }
        var signedHeaders = string.Join(";", headers.Keys);
        var canonical = new StringBuilder()
            .Append(req.Method.Method).Append('\n')
            .Append(canonicalPath.Length == 0 ? "/" : canonicalPath).Append('\n')
            .Append(canonicalQuery).Append('\n');
        foreach (var (k, v) in headers) canonical.Append(k).Append(':').Append(v).Append('\n');
        canonical.Append('\n').Append(signedHeaders).Append('\n').Append(payloadHash);

        var scope = $"{date}/{Region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))}";
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + c.SecretAccessKey), Encoding.UTF8.GetBytes(date));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(Region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes("s3"));
        var kSigning = HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
        var signature = Hex(HMACSHA256.HashData(kSigning, Encoding.UTF8.GetBytes(stringToSign)));
        req.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={c.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    public static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    public static string? XmlValue(string text, string el)
    {
        int a = text.IndexOf("<" + el + ">", StringComparison.Ordinal);
        if (a < 0) return null;
        int b = text.IndexOf("</" + el + ">", a, StringComparison.Ordinal);
        return b > a ? WebUtility.HtmlDecode(text[(a + el.Length + 2)..b]) : null;
    }
}

/// <summary>A bucket (and optional prefix) in Amazon S3 or an S3-compatible service.</summary>
public sealed class S3Store : IDocumentStore
{
    private const int PartSize = 8 * 1024 * 1024;   // multipart threshold and part size (S3 minimum part is 5 MiB)

    public S3Account Account { get; }
    public string Bucket { get; }
    public string Prefix { get; }   // "" or ends with '/'

    public S3Store(S3Account account, string bucket, string? prefix)
    {
        Account = account;
        Bucket = (bucket ?? "").Trim().Trim('/');
        if (Bucket.Length == 0) throw new ArgumentException("Bucket is required.");
        var p = (prefix ?? "").Trim().TrimStart('/');
        Prefix = p.Length == 0 || p.EndsWith('/') ? p : p + "/";
    }

    public string Kind => StorageProviders.S3;
    public string Describe => $"{Account.Name}/{Bucket}/{Prefix}";
    private string Name(string key) => Prefix + key;

    private static string? Child(XmlNode n, string name) => n.ChildNodes.Cast<XmlNode>().FirstOrDefault(c => c.LocalName == name)?.InnerText;
    private static XmlDocument Parse(string xml) { var d = new XmlDocument { XmlResolver = null }; d.LoadXml(xml); return d; }

    public async IAsyncEnumerable<SourceFile> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        string? token = null;
        do
        {
            using var resp = await Account.SendAsync(HttpMethod.Get, Bucket, "",
                S3Account.Q(("list-type", "2"), ("max-keys", "1000"), ("prefix", Prefix.Length > 0 ? Prefix : null), ("continuation-token", token)), null, null, ct);
            var root = Parse(await resp.Content.ReadAsStringAsync(ct)).DocumentElement!;
            var list = new List<SourceFile>();
            foreach (XmlNode c in root.ChildNodes)
            {
                if (c.LocalName != "Contents") continue;
                var name = Child(c, "Key") ?? "";
                long.TryParse(Child(c, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                if (name.EndsWith('/') && size == 0) continue; // folder placeholder
                var cls = Child(c, "StorageClass");
                list.Add(new SourceFile
                {
                    Key = name[Prefix.Length..],
                    Size = size,
                    ETag = Child(c, "ETag"),
                    // Glacier Flexible Retrieval and Deep Archive objects cannot be read until restored.
                    AccessTier = cls is "GLACIER" or "DEEP_ARCHIVE" ? "Archive" : cls,
                    LastModified = DateTimeOffset.TryParse(Child(c, "LastModified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lm) ? lm : null
                });
            }
            foreach (var f in list) yield return f;
            token = Child(root, "IsTruncated") == "true" ? Child(root, "NextContinuationToken") : null;
            if (string.IsNullOrEmpty(token)) token = null;
        } while (token != null);
    }

    public async Task<FileProps?> GetPropsAsync(string key, CancellationToken ct)
    {
        try
        {
            using var resp = await Account.SendAsync(HttpMethod.Head, Bucket, Name(key), null, null, null, ct);
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in resp.Headers)
                if (h.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)) meta[h.Key[11..].ToLowerInvariant()] = h.Value.First();
            var etag = resp.Headers.ETag?.Tag ?? (resp.Headers.TryGetValues("ETag", out var ev) ? ev.First() : null);
            return new FileProps(resp.Content.Headers.ContentLength ?? 0, etag, Md5FromETag(etag, resp), meta);
        }
        catch (S3RequestException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>
    /// For single-part uploads without KMS or customer-key encryption the ETag is the hex MD5 of the content.
    /// Returned as base64 (the Content-MD5 format) so it compares with Azure's Content-MD5.
    /// </summary>
    private static string? Md5FromETag(string? etag, HttpResponseMessage resp)
    {
        if (etag == null) return null;
        var hex = etag.Trim('"');
        if (hex.Length != 32 || !hex.All(Uri.IsHexDigit)) return null;
        if (resp.Headers.TryGetValues("x-amz-server-side-encryption", out var sse) && sse.First().StartsWith("aws:kms", StringComparison.OrdinalIgnoreCase)) return null;
        if (resp.Headers.Contains("x-amz-server-side-encryption-customer-algorithm")) return null;
        return Convert.ToBase64String(Convert.FromHexString(hex));
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var resp = await Account.SendAsync(HttpMethod.Get, Bucket, Name(key), null, null, null, ct, HttpCompletionOption.ResponseHeadersRead);
        return new OwnedStream(await resp.Content.ReadAsStreamAsync(ct), resp);
    }

    private static void AddMeta(HttpRequestMessage req, IDictionary<string, string>? metadata)
    {
        if (metadata == null) return;
        foreach (var (k, v) in metadata)
        {
            var name = new string(k.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
            if (name.Length > 0) req.Headers.TryAddWithoutValidation("x-amz-meta-" + name, StreamUtil.EncodeMetaValue(v));
        }
    }

    private static void Body(HttpRequestMessage r, byte[] data)
    {
        r.Content!.Headers.ContentMD5 = MD5.HashData(data);
    }

    public async Task WriteAsync(string key, Stream content, IDictionary<string, string>? metadata, bool overwrite, CancellationToken ct)
    {
        var buffer = new byte[PartSize];
        int first = await StreamUtil.ReadFull(content, buffer, ct);
        if (first < PartSize)
        {
            var data = buffer.AsSpan(0, first).ToArray();
            async Task Put(bool conditional)
            {
                using var _ = await Account.SendAsync(HttpMethod.Put, Bucket, Name(key), null, r =>
                {
                    Body(r, data);
                    r.Content!.Headers.ContentType = new("application/octet-stream");
                    if (conditional) r.Headers.TryAddWithoutValidation("If-None-Match", "*");
                    AddMeta(r, metadata);
                }, data, ct);
            }
            try { await Put(!overwrite); }
            catch (S3RequestException ex) when (!overwrite && ex.Status is 412 or 409) { throw new DestinationExistsException(key); }
            catch (S3RequestException ex) when (!overwrite && ex.Status == 501)
            {
                // S3-compatible service without conditional writes: check, then write.
                if (await GetPropsAsync(key, ct) != null) throw new DestinationExistsException(key);
                await Put(false);
            }
            return;
        }

        if (!overwrite && await GetPropsAsync(key, ct) != null) throw new DestinationExistsException(key);
        string uploadId;
        using (var init = await Account.SendAsync(HttpMethod.Post, Bucket, Name(key), S3Account.Q(("uploads", "")), r =>
               {
                   r.Content!.Headers.ContentType = new("application/octet-stream");
                   AddMeta(r, metadata);
               }, null, ct))
            uploadId = S3Account.XmlValue(await init.Content.ReadAsStringAsync(ct), "UploadId") ?? throw new InvalidOperationException("S3 did not return an UploadId.");
        try
        {
            var parts = new List<(int n, string etag)>();
            int len = first;
            while (len > 0)
            {
                int n = parts.Count + 1;
                if (n > 10000) throw new InvalidOperationException("File is too large for a multipart upload with 8 MiB parts (over 10,000 parts).");
                var chunk = buffer.AsSpan(0, len).ToArray();
                using (var pr = await Account.SendAsync(HttpMethod.Put, Bucket, Name(key), S3Account.Q(("partNumber", n.ToString(CultureInfo.InvariantCulture)), ("uploadId", uploadId)),
                           r => Body(r, chunk), chunk, ct))
                    parts.Add((n, pr.Headers.ETag?.Tag ?? pr.Headers.GetValues("ETag").First()));
                len = await StreamUtil.ReadFull(content, buffer, ct);
            }
            var xml = "<CompleteMultipartUpload xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
                      string.Concat(parts.Select(p => $"<Part><PartNumber>{p.n}</PartNumber><ETag>{SecurityElement.Escape(p.etag)}</ETag></Part>")) +
                      "</CompleteMultipartUpload>";
            var body = Encoding.UTF8.GetBytes(xml);
            using var done = await Account.SendAsync(HttpMethod.Post, Bucket, Name(key), S3Account.Q(("uploadId", uploadId)), r =>
            {
                r.Content!.Headers.ContentType = new("application/xml");
                if (!overwrite) r.Headers.TryAddWithoutValidation("If-None-Match", "*");
            }, body, ct);
            // CompleteMultipartUpload can return 200 with an error document.
            var text = await done.Content.ReadAsStringAsync(ct);
            if (text.Contains("<Error>", StringComparison.Ordinal))
                throw new S3RequestException(200, S3Account.XmlValue(text, "Code") ?? "InternalError", S3Account.XmlValue(text, "Message") ?? "Multipart upload did not complete.");
        }
        catch (Exception ex)
        {
            try { using var _ = await Account.SendAsync(HttpMethod.Delete, Bucket, Name(key), S3Account.Q(("uploadId", uploadId)), null, null, CancellationToken.None); } catch { }
            if (ex is S3RequestException se && !overwrite && se.Status == 412) throw new DestinationExistsException(key);
            throw;
        }
    }

    public async Task<bool> TryServerCopyAsync(IDocumentStore source, string sourceKey, string? sourceETag, string destKey,
        IDictionary<string, string>? metadata, bool overwrite, CancellationToken ct)
    {
        // Server-side copy needs one set of credentials that can read the source and write the destination.
        if (source is not S3Store src || !ReferenceEquals(src.Account, Account)) return false;
        if (!overwrite && await GetPropsAsync(destKey, ct) != null) throw new DestinationExistsException(destKey);
        try
        {
            using var resp = await Account.SendAsync(HttpMethod.Put, Bucket, Name(destKey), null, r =>
            {
                r.Headers.TryAddWithoutValidation("x-amz-copy-source", "/" + src.Bucket + "/" + S3Account.EncPath(src.Prefix + sourceKey));
                if (sourceETag != null) r.Headers.TryAddWithoutValidation("x-amz-copy-source-if-match", sourceETag);
                r.Headers.TryAddWithoutValidation("x-amz-metadata-directive", metadata != null ? "REPLACE" : "COPY");
                AddMeta(r, metadata);
            }, null, ct);
            // CopyObject can return 200 with an error document.
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (text.Contains("<Error>", StringComparison.Ordinal))
                throw new S3RequestException(200, S3Account.XmlValue(text, "Code") ?? "InternalError", S3Account.XmlValue(text, "Message") ?? "Copy did not complete.");
        }
        catch (S3RequestException ex) when (ex.Status == 412 || ex.Code == "PreconditionFailed") { throw new SourceChangedException(sourceKey); }
        catch (S3RequestException ex) when (ex.Code is "NotImplemented" or "EntityTooLarge" or "AccessDenied"
                                            || ex.Code == "InvalidRequest" && ex.Message.Contains("copy source is larger", StringComparison.OrdinalIgnoreCase))
        { return false; } // falls back to streaming (multipart for large files)
        return true;
    }

    public async Task<string> TestAsync(bool needWrite, CancellationToken ct)
    {
        if (needWrite)
        {
            // Create the bucket if it does not exist and the credentials allow it, then write and delete a probe object.
            bool exists = true;
            try { using var _ = await Account.SendAsync(HttpMethod.Head, Bucket, "", null, null, null, ct); }
            catch (S3RequestException ex) when (ex.Status == 404) { exists = false; }
            catch (S3RequestException ex) when (ex.Status == 403) { /* may lack ListBucket; try the write anyway */ }
            if (!exists)
            {
                var cfg = Account.Region == "us-east-1" ? null : Encoding.UTF8.GetBytes(
                    $"<CreateBucketConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><LocationConstraint>{Account.Region}</LocationConstraint></CreateBucketConfiguration>");
                try { using var _ = await Account.SendAsync(HttpMethod.Put, Bucket, "", null, null, cfg, ct); }
                catch (S3RequestException ex) when (ex.Code is "BucketAlreadyOwnedByYou") { }
            }
            var probe = Prefix + ".docorg-write-test-" + RandomNumberGenerator.GetHexString(6).ToLowerInvariant();
            var ok = Encoding.ASCII.GetBytes("ok");
            using (await Account.SendAsync(HttpMethod.Put, Bucket, probe, null, r => Body(r, ok), ok, ct)) { }
            using (await Account.SendAsync(HttpMethod.Delete, Bucket, probe, null, null, null, ct)) { }
            return $"Write access confirmed for {Describe} ({Account.AuthDescription}).";
        }
        using var resp = await Account.SendAsync(HttpMethod.Get, Bucket, "",
            S3Account.Q(("list-type", "2"), ("max-keys", "1"), ("prefix", Prefix.Length > 0 ? Prefix : null)), null, null, ct);
        var xml = await resp.Content.ReadAsStringAsync(ct);
        bool any = xml.Contains("<Contents>", StringComparison.Ordinal);
        return $"List and read access confirmed for {Describe} ({Account.AuthDescription}).{(any ? "" : " No objects found under this prefix yet.")}";
    }
}
