using System.Globalization;
using System.Text.Json;
using System.Xml;

namespace DocumentOrganizer.Storage;

public sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken, DateTimeOffset? Expires);

/// <summary>Supplies AWS credentials, refreshing temporary ones before they expire.</summary>
public interface IAwsCredentialSource
{
    string Description { get; }
    Task<AwsCredentials> GetAsync(CancellationToken ct);
}

/// <summary>Base class that caches temporary credentials and refreshes them 5 minutes before expiry.</summary>
public abstract class CachedAwsCredentialSource : IAwsCredentialSource
{
    protected static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private AwsCredentials? _creds;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public abstract string Description { get; }
    protected abstract Task<AwsCredentials> FetchAsync(CancellationToken ct);

    public async Task<AwsCredentials> GetAsync(CancellationToken ct)
    {
        if (Fresh(_creds)) return _creds!;
        await _lock.WaitAsync(ct);
        try
        {
            if (Fresh(_creds)) return _creds!;
            return _creds = await FetchAsync(ct);
        }
        finally { _lock.Release(); }
    }

    private static bool Fresh(AwsCredentials? c) => c != null && (c.Expires == null || c.Expires > DateTimeOffset.UtcNow.AddMinutes(5));

    protected static AwsCredentials FromJson(string json, string what)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var ak = S("AccessKeyId") ?? throw new InvalidOperationException($"{what}: response has no AccessKeyId.");
        var sk = S("SecretAccessKey") ?? throw new InvalidOperationException($"{what}: response has no SecretAccessKey.");
        DateTimeOffset? exp = DateTimeOffset.TryParse(S("Expiration"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e) ? e : null;
        return new AwsCredentials(ak, sk, S("Token") ?? S("SessionToken"), exp);
    }
}

public sealed class StaticAwsCredentials(string accessKeyId, string secretAccessKey, string? sessionToken) : IAwsCredentialSource
{
    private readonly AwsCredentials _c = new(accessKeyId, secretAccessKey, sessionToken, null);
    public string Description => "Access key (configuration)";
    public Task<AwsCredentials> GetAsync(CancellationToken ct) => Task.FromResult(_c);
}

/// <summary>AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY and optional AWS_SESSION_TOKEN.</summary>
public sealed class EnvironmentAwsCredentials : IAwsCredentialSource
{
    public string Description => "Environment variables (AWS_ACCESS_KEY_ID)";
    public static bool Available => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"))
                                    && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"));
    public Task<AwsCredentials> GetAsync(CancellationToken ct)
    {
        if (!Available) throw new InvalidOperationException("AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY are not set.");
        return Task.FromResult(new AwsCredentials(Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID")!,
            Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")!, NullIfEmpty(Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN")), null));
    }
    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>Static keys from the shared credentials file (~/.aws/credentials or AWS_SHARED_CREDENTIALS_FILE).</summary>
public sealed class ProfileAwsCredentials(string? profile) : IAwsCredentialSource
{
    private readonly string _profile = profile ?? Environment.GetEnvironmentVariable("AWS_PROFILE") ?? "default";
    public string Description => $"Shared credentials file (profile '{_profile}')";

    public static string FilePath => Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "credentials");

    public bool Available => TryRead(out _);

    private bool TryRead(out AwsCredentials? creds)
    {
        creds = null;
        if (!File.Exists(FilePath)) return false;
        string? section = null; var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(FilePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            int i = line.IndexOf('=');
            if (i > 0 && section == _profile) values[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        if (!values.TryGetValue("aws_access_key_id", out var ak) || !values.TryGetValue("aws_secret_access_key", out var sk)) return false;
        creds = new AwsCredentials(ak, sk, values.GetValueOrDefault("aws_session_token"), null);
        return true;
    }

    public Task<AwsCredentials> GetAsync(CancellationToken ct) =>
        TryRead(out var c) ? Task.FromResult(c!) : throw new InvalidOperationException($"Profile '{_profile}' with static keys was not found in {FilePath}.");
}

/// <summary>EKS IAM Roles for Service Accounts and other OIDC federation: AWS_WEB_IDENTITY_TOKEN_FILE + AWS_ROLE_ARN.</summary>
public sealed class WebIdentityAwsCredentials(string region) : CachedAwsCredentialSource
{
    public override string Description => "Web identity role (AWS_ROLE_ARN)";
    public static bool Available => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_WEB_IDENTITY_TOKEN_FILE"))
                                    && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_ROLE_ARN"));

    protected override async Task<AwsCredentials> FetchAsync(CancellationToken ct)
    {
        if (!Available) throw new InvalidOperationException("AWS_WEB_IDENTITY_TOKEN_FILE / AWS_ROLE_ARN are not set.");
        var token = (await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("AWS_WEB_IDENTITY_TOKEN_FILE")!, ct)).Trim();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Action"] = "AssumeRoleWithWebIdentity", ["Version"] = "2011-06-15",
            ["RoleArn"] = Environment.GetEnvironmentVariable("AWS_ROLE_ARN")!,
            ["RoleSessionName"] = Environment.GetEnvironmentVariable("AWS_ROLE_SESSION_NAME") ?? "document-organizer",
            ["WebIdentityToken"] = token
        });
        using var resp = await Http.PostAsync($"https://sts.{region}.amazonaws.com/", form, ct);
        var xml = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"AssumeRoleWithWebIdentity failed ({(int)resp.StatusCode}).");
        var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(xml);
        string? V(string n) => doc.GetElementsByTagName(n).Cast<XmlNode>().FirstOrDefault()?.InnerText;
        DateTimeOffset? exp = DateTimeOffset.TryParse(V("Expiration"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e) ? e : null;
        return new AwsCredentials(V("AccessKeyId") ?? throw new InvalidOperationException("STS response has no AccessKeyId."), V("SecretAccessKey")!, V("SessionToken"), exp);
    }
}

/// <summary>ECS task role / EKS Pod Identity / App Runner: AWS_CONTAINER_CREDENTIALS_RELATIVE_URI or _FULL_URI.</summary>
public sealed class ContainerAwsCredentials : CachedAwsCredentialSource
{
    public override string Description => "Container role (ECS / EKS Pod Identity)";
    public static bool Available => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI"))
                                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_FULL_URI"));

    protected override async Task<AwsCredentials> FetchAsync(CancellationToken ct)
    {
        var rel = Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI");
        var url = !string.IsNullOrEmpty(rel) ? "http://169.254.170.2" + rel : Environment.GetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_FULL_URI");
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("No container credentials endpoint is configured.");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var auth = Environment.GetEnvironmentVariable("AWS_CONTAINER_AUTHORIZATION_TOKEN");
        var authFile = Environment.GetEnvironmentVariable("AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE");
        if (!string.IsNullOrEmpty(authFile)) auth = (await File.ReadAllTextAsync(authFile, ct)).Trim();
        if (!string.IsNullOrEmpty(auth)) req.Headers.TryAddWithoutValidation("Authorization", auth);
        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Container credentials request failed ({(int)resp.StatusCode}).");
        return FromJson(json, "Container credentials");
    }
}

/// <summary>EC2 instance profile through the instance metadata service (IMDSv2).</summary>
public sealed class InstanceProfileAwsCredentials : CachedAwsCredentialSource
{
    private const string Imds = "http://169.254.169.254";
    public override string Description => "EC2 instance role (IMDSv2)";

    protected override async Task<AwsCredentials> FetchAsync(CancellationToken ct)
    {
        try
        {
            using var tokReq = new HttpRequestMessage(HttpMethod.Put, Imds + "/latest/api/token");
            tokReq.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token-ttl-seconds", "21600");
            using var tokResp = await Http.SendAsync(tokReq, ct);
            var token = tokResp.IsSuccessStatusCode ? await tokResp.Content.ReadAsStringAsync(ct) : null;
            async Task<string> Get(string path)
            {
                using var r = new HttpRequestMessage(HttpMethod.Get, Imds + path);
                if (token != null) r.Headers.TryAddWithoutValidation("X-aws-ec2-metadata-token", token);
                using var resp = await Http.SendAsync(r, ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Instance metadata returned {(int)resp.StatusCode} for {path}. Is an IAM role attached to this instance?");
                return await resp.Content.ReadAsStringAsync(ct);
            }
            var role = (await Get("/latest/meta-data/iam/security-credentials/")).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
                       ?? throw new InvalidOperationException("No IAM role is attached to this instance.");
            return FromJson(await Get("/latest/meta-data/iam/security-credentials/" + role), "Instance metadata");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("No AWS credentials found. Set AccessKeyId/SecretAccessKey on the connection, AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY, a profile in ~/.aws/credentials, or run on AWS with an IAM role.");
        }
    }
}

/// <summary>The standard AWS lookup order: environment, web identity, shared profile, container role, instance role.</summary>
public sealed class DefaultAwsCredentials(string region, string? profile) : IAwsCredentialSource
{
    private IAwsCredentialSource? _chosen;
    public string Description => _chosen != null ? "Default chain: " + _chosen.Description : "Default AWS credential chain";

    private IAwsCredentialSource Choose()
    {
        if (EnvironmentAwsCredentials.Available) return new EnvironmentAwsCredentials();
        if (WebIdentityAwsCredentials.Available) return new WebIdentityAwsCredentials(region);
        var p = new ProfileAwsCredentials(profile);
        if (p.Available) return p;
        if (ContainerAwsCredentials.Available) return new ContainerAwsCredentials();
        return new InstanceProfileAwsCredentials();
    }

    public Task<AwsCredentials> GetAsync(CancellationToken ct) => (_chosen ??= Choose()).GetAsync(ct);
}
