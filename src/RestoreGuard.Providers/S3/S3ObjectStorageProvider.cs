using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace RestoreGuard.Providers.S3;

/// <summary>One read-only S3-compatible bucket to audit. Credentials use the same
/// inline-or-file pattern as reporting destinations; files keep values out of JSON.</summary>
public sealed record S3BucketConfig(
    string Name,
    string Endpoint,
    string Bucket,
    string Prefix = "",
    string Region = "us-east-1",
    bool ForcePathStyle = true,
    string? AccessKey = null,
    string? AccessKeyFile = null,
    string? SecretKey = null,
    string? SecretKeyFile = null,
    bool CheckObjectLock = true,
    bool ObjectLockRequired = true,
    int? MinRetentionDays = null,
    bool CheckNewestObject = false,
    double MaxNewestObjectAgeHours = 26,
    int MaxListKeys = 100,
    int MaxListRequests = 5);

public sealed record S3VersioningState(string? Status, bool MfaDeleteEnabled)
{
    public bool VersioningEnabled => Status == "Enabled";
}

public sealed record S3ObjectLockState(
    bool Enabled,
    string? Mode,
    int? Days,
    int? Years)
{
    public bool HasDefaultRetention => Mode is not null && (Days is > 0 || Years is > 0);
    public int? RetentionDays => Mode is null ? null : (Days ?? 0) + (Years ?? 0) * 365;
}

public sealed record S3ObjectSummary(string Key, DateTimeOffset LastModified, long? SizeBytes);

public sealed record S3ListPage(
    IReadOnlyList<S3ObjectSummary> Objects,
    string? NextContinuationToken,
    bool IsTruncated);

/// <summary>Provider result passed to the deterministic check. Nothing here performs I/O.</summary>
public sealed record S3BucketAudit(
    string Name,
    string Bucket,
    S3VersioningState Versioning,
    S3ObjectLockState? ObjectLock,
    S3ObjectSummary? NewestObject,
    bool NewestObjectListingIncomplete);

public interface IObjectStorageProvider
{
    Task<S3BucketAudit> GetBucketAsync(
        S3BucketConfig config,
        string configDir,
        CancellationToken ct = default);
}

/// <summary>Reads bucket metadata with signed GET requests. It never writes a probe
/// object, never reads object bodies, and stops list pagination at the configured cap.</summary>
public sealed class S3ObjectStorageProvider(HttpMessageHandler? handler = null) : IObjectStorageProvider
{
    private readonly HttpClient _http = new(handler ?? new HttpClientHandler())
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public async Task<S3BucketAudit> GetBucketAsync(
        S3BucketConfig config, string configDir, CancellationToken ct = default)
    {
        var versioning = ParseVersioning(
            await GetAsync(config, configDir, new Dictionary<string, string> { ["versioning"] = "" }, ct));

        S3ObjectLockState? objectLock = null;
        if (config.CheckObjectLock)
        {
            objectLock = await GetObjectLockAsync(config, configDir, ct);
        }

        S3ObjectSummary? newest = null;
        var incomplete = false;
        if (config.CheckNewestObject)
        {
            var pageSize = Math.Clamp(config.MaxListKeys, 1, 1000);
            var remainingRequests = Math.Max(1, config.MaxListRequests);
            string? token = null;
            while (true)
            {
                var query = new Dictionary<string, string>
                {
                    ["list-type"] = "2",
                    ["max-keys"] = pageSize.ToString(CultureInfo.InvariantCulture),
                };
                if (config.Prefix.Length > 0)
                    query["prefix"] = config.Prefix;
                if (token is not null)
                    query["continuation-token"] = token;

                var page = ParseListPage(await GetAsync(config, configDir, query, ct));
                var pageNewest = page.Objects
                    .OrderByDescending(o => o.LastModified)
                    .ThenBy(o => o.Key, StringComparer.Ordinal)
                    .Cast<S3ObjectSummary?>()
                    .FirstOrDefault();
                if (pageNewest is not null &&
                    (newest is null || pageNewest.LastModified > newest.LastModified ||
                     (pageNewest.LastModified == newest.LastModified &&
                      string.CompareOrdinal(pageNewest.Key, newest.Key) < 0)))
                    newest = pageNewest;

                remainingRequests--;
                if (!page.IsTruncated)
                    break;
                if (string.IsNullOrEmpty(page.NextContinuationToken) || remainingRequests == 0)
                {
                    incomplete = true;
                    break;
                }
                token = page.NextContinuationToken;
            }
        }

        return new(config.Name, config.Bucket, versioning, objectLock, newest, incomplete);
    }

    private async Task<string> GetAsync(
        S3BucketConfig config,
        string configDir,
        IReadOnlyDictionary<string, string> query,
        CancellationToken ct)
    {
        var endpoint = new Uri(config.Endpoint, UriKind.Absolute);
        var host = config.ForcePathStyle
            ? endpoint.Authority
            : $"{config.Bucket}.{endpoint.Authority}";
        var canonicalPath = config.ForcePathStyle ? $"/{AwsSigV4.UriEncode(config.Bucket)}" : "/";
        var canonicalQuery = CanonicalQuery(query);

        var now = DateTimeOffset.UtcNow;
        var payloadHash = AwsSigV4.Sha256Hex([]);
        var headers = new Dictionary<string, string>
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = AwsSigV4.AmzDate(now),
        };
        var authorization = AwsSigV4.AuthorizationHeader(
            HttpMethod.Get.Method, canonicalPath, canonicalQuery, headers, payloadHash, now,
            config.Region, "s3",
            ResolveSecret(config.AccessKey, config.AccessKeyFile, configDir, "S3 access key"),
            ResolveSecret(config.SecretKey, config.SecretKeyFile, configDir, "S3 secret key"));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.Scheme}://{host}{canonicalPath}?{canonicalQuery}");
        request.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            if (detail.Length > 300)
                detail = detail[..300] + "…";
            throw new S3RequestException(response.StatusCode, detail,
                $"S3 GET {config.Bucket} failed: {(int)response.StatusCode} {response.ReasonPhrase} {detail}".TrimEnd());
        }
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<S3ObjectLockState> GetObjectLockAsync(
        S3BucketConfig config, string configDir, CancellationToken ct)
    {
        try
        {
            return ParseObjectLock(await GetAsync(
                config, configDir, new Dictionary<string, string> { ["object-lock"] = "" }, ct));
        }
        catch (S3RequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound &&
                                             ex.ResponseBody.Contains("ObjectLockConfigurationNotFoundError", StringComparison.Ordinal))
        {
            return new(false, null, null, null);
        }
    }

    private static string CanonicalQuery(IReadOnlyDictionary<string, string> query) =>
        string.Join("&", query
            .Select(pair => (Key: pair.Key, Value: pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{AwsSigV4.UriEncode(pair.Key)}={AwsSigV4.UriEncode(pair.Value)}"));

    private static string ResolveSecret(string? inline, string? file, string configDir, string what)
    {
        if (!string.IsNullOrWhiteSpace(inline))
            return inline.Trim();
        if (string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException($"{what} is not configured.");
        var path = Path.IsPathRooted(file) ? file : Path.Combine(configDir, file);
        if (!File.Exists(path))
            throw new InvalidOperationException($"{what} file not found: {path}");
        return File.ReadAllText(path).Trim();
    }

    public static S3VersioningState ParseVersioning(string xml)
    {
        var root = Parse(xml, "VersioningConfiguration");
        return new(
            Value(root, "Status"),
            Value(root, "MFADelete") == "Enabled");
    }

    public static S3ObjectLockState ParseObjectLock(string xml)
    {
        var root = Parse(xml, "ObjectLockConfiguration");
        var rule = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Rule");
        if (rule is null)
            return new(Value(root, "ObjectLockEnabled") == "Enabled", null, null, null);
        var mode = rule.Descendants().FirstOrDefault(e => e.Name.LocalName == "Mode")?.Value;
        var days = Int32OrNull(rule, "Days");
        var years = Int32OrNull(rule, "Years");
        return new(
            Value(root, "ObjectLockEnabled") == "Enabled",
            mode,
            days,
            years);
    }

    public static S3ListPage ParseListPage(string xml)
    {
        var root = Parse(xml, "ListBucketResult");
        var objects = root.Descendants()
            .Where(e => e.Name.LocalName == "Contents")
            .Select(e => new S3ObjectSummary(
                e.Descendants().First(d => d.Name.LocalName == "Key").Value,
                DateTimeOffset.Parse(
                    e.Descendants().First(d => d.Name.LocalName == "LastModified").Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                LongOrNull(e, "Size")))
            .ToList();
        var token = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "NextContinuationToken")?.Value;
        var truncated = root.Descendants().Any(e => e.Name.LocalName == "IsTruncated" && e.Value == "true");
        return new(objects, token, truncated);
    }

    private static XElement Parse(string xml, string rootName)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root;
        if (root is null || root.Name.LocalName != rootName)
            throw new InvalidOperationException($"S3 response is not {rootName} XML.");
        return root;
    }

    private static string? Value(XElement root, string localName) =>
        root.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static int? Int32OrNull(XElement root, string localName) =>
        int.TryParse(Value(root, localName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static long? LongOrNull(XElement element, string localName) =>
        long.TryParse(
            element.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private sealed class S3RequestException(HttpStatusCode statusCode, string responseBody, string message)
        : InvalidOperationException(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
        public string ResponseBody { get; } = responseBody;
    }
}
