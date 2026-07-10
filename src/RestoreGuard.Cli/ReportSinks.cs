using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace RestoreGuard.Cli;

/// <summary>
/// One destination a finished report is delivered to. PublishAsync returns a
/// one-line human detail ("wrote /path/rg-report-....json"); VerifyAsync proves
/// write access up front (doctor, wizard probes) and throws with a plain-language
/// message on failure.
/// </summary>
public interface IReportSink
{
    /// <summary>Short label for progress lines, e.g. "folder /var/lib/restoreguard/reports".</summary>
    string Description { get; }

    /// <summary>Stable connection id for this destination — stamped into each report's
    /// <c>destinations</c> metadata so a report is self-describing about where it lives.
    /// Comes from the config's <c>id</c>, or a type-derived default.</summary>
    string Id { get; }

    Task<string> PublishAsync(string reportJson, DateTimeOffset generatedAt, CancellationToken ct = default);

    Task VerifyAsync(CancellationToken ct = default);
}

/// <summary>Resolves an inline-or-file secret pair (the passwordFile pattern the
/// rest of the config already uses). Relative file paths resolve against the
/// config file's directory.</summary>
public static class SinkSecrets
{
    public static string Resolve(string? inline, string? file, string configDir, string what)
    {
        if (!string.IsNullOrWhiteSpace(inline))
            return inline.Trim();
        if (string.IsNullOrWhiteSpace(file))
            throw new InvalidOperationException($"{what} is not configured (value or file).");
        var path = Path.IsPathRooted(file) ? file : Path.Combine(configDir, file);
        if (!File.Exists(path))
            throw new InvalidOperationException($"{what} file not found: {path}");
        return File.ReadAllText(path).Trim();
    }
}

/// <summary>
/// Writes rg-report-&lt;utc-ts&gt;.json per audit plus an atomically-replaced
/// latest.json — the stable filename a consumer script reads. Files appear via
/// dot-prefixed temp + rename, so a file watcher never sees a half-written report.
/// </summary>
public sealed class FolderReportSink(string folder, int? keepLast, string? id = null) : IReportSink
{
    public string Description => $"folder {folder}";
    public string Id => id ?? "folder";

    public Task<string> PublishAsync(string reportJson, DateTimeOffset generatedAt, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var name = $"rg-report-{generatedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.json";
        WriteAtomic(name, reportJson);
        WriteAtomic("latest.json", reportJson);
        Prune();
        return Task.FromResult($"wrote {Path.Combine(folder, name)} (+ latest.json)");
    }

    public Task VerifyAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".rg-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"reports folder {folder} is not writable: {ex.Message}");
        }
    }

    private void WriteAtomic(string name, string content)
    {
        var tmp = Path.Combine(folder, $".{name}.tmp");
        File.WriteAllText(tmp, content);
        File.Move(tmp, Path.Combine(folder, name), overwrite: true);
    }

    private void Prune()
    {
        if (keepLast is not > 0)
            return;
        // Timestamped names sort chronologically, so ordinal-descending = newest first.
        var stale = Directory.GetFiles(folder, "rg-report-*.json")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(keepLast.Value);
        foreach (var file in stale)
            File.Delete(file);
    }
}

/// <summary>
/// Puts the report into any S3-compatible bucket: the timestamped key plus a
/// stable &lt;prefix&gt;latest.json. Talks plain HTTP + SigV4 — no SDK.
/// </summary>
public sealed class S3ReportSink : IReportSink
{
    private readonly S3SinkConfig _config;
    private readonly string _configDir;
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;

    public S3ReportSink(S3SinkConfig config, string configDir,
        HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        _config = config;
        _configDir = configDir;
        _http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(60) };
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Description => $"s3 {_config.Endpoint.TrimEnd('/')}/{_config.Bucket}";
    public string Id => _config.Id ?? $"s3:{_config.Bucket}";

    public async Task<string> PublishAsync(string reportJson, DateTimeOffset generatedAt, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(reportJson);
        var key = $"{Prefix()}rg-report-{generatedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.json";
        await SendAsync(HttpMethod.Put, key, payload, ct);
        await SendAsync(HttpMethod.Put, $"{Prefix()}latest.json", payload, ct);
        return $"put {key} (+ {Prefix()}latest.json)";
    }

    public async Task VerifyAsync(CancellationToken ct = default)
    {
        var probeKey = $"{Prefix()}.rg-write-test";
        await SendAsync(HttpMethod.Put, probeKey, Encoding.UTF8.GetBytes("ok"), ct);
        await SendAsync(HttpMethod.Delete, probeKey, null, ct);
    }

    private string Prefix()
    {
        var p = _config.Prefix.Trim().TrimStart('/');
        return p.Length == 0 || p.EndsWith('/') ? p : p + "/";
    }

    private async Task SendAsync(HttpMethod method, string key, byte[]? payload, CancellationToken ct)
    {
        var endpoint = new Uri(_config.Endpoint, UriKind.Absolute);
        var host = _config.ForcePathStyle ? endpoint.Authority : $"{_config.Bucket}.{endpoint.Authority}";
        var canonicalPath = (_config.ForcePathStyle ? $"/{AwsSigV4.UriEncode(_config.Bucket)}" : "")
            + "/" + string.Join('/', key.Split('/').Select(AwsSigV4.UriEncode));

        var now = _clock();
        var payloadHash = AwsSigV4.Sha256Hex(payload ?? []);
        var headers = new Dictionary<string, string>
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = AwsSigV4.AmzDate(now),
        };
        var authorization = AwsSigV4.AuthorizationHeader(
            method.Method, canonicalPath, canonicalQuery: "", headers, payloadHash, now,
            _config.Region, "s3",
            SinkSecrets.Resolve(_config.AccessKey, _config.AccessKeyFile, _configDir, "reporting.s3 access key"),
            SinkSecrets.Resolve(_config.SecretKey, _config.SecretKeyFile, _configDir, "reporting.s3 secret key"));

        using var request = new HttpRequestMessage(method, $"{endpoint.Scheme}://{host}{canonicalPath}");
        request.Headers.TryAddWithoutValidation("x-amz-date", headers["x-amz-date"]);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (payload is not null)
            request.Content = new ByteArrayContent(payload);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"S3 endpoint {endpoint} unreachable: {ex.Message}");
        }
        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;
            var body = await response.Content.ReadAsStringAsync(ct);
            var detail = body.Length > 300 ? body[..300] + "..." : body;
            throw new InvalidOperationException(
                $"S3 {method.Method} {canonicalPath} failed: {(int)response.StatusCode} {response.ReasonPhrase} {detail}".TrimEnd());
        }
    }
}

/// <summary>Inserts each report as one document into a MongoDB collection —
/// history becomes queryable (by generatedAt, overall, findings.ruleId, ...).</summary>
public sealed class MongoReportSink(MongoSinkConfig config, string configDir) : IReportSink
{
    public string Description => $"mongo {config.Database}.{config.Collection}";
    public string Id => config.Id ?? $"mongo:{config.Database}.{config.Collection}";

    public async Task<string> PublishAsync(string reportJson, DateTimeOffset generatedAt, CancellationToken ct = default)
    {
        var document = BsonDocument.Parse(reportJson);
        await Collection().InsertOneAsync(document, cancellationToken: ct);
        return $"inserted report into {config.Database}.{config.Collection}";
    }

    public async Task VerifyAsync(CancellationToken ct = default)
    {
        try
        {
            await Client().GetDatabase(config.Database)
                .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            throw new InvalidOperationException($"MongoDB not reachable with the configured connection string: {ex.Message}");
        }
    }

    private IMongoCollection<BsonDocument> Collection() =>
        Client().GetDatabase(config.Database).GetCollection<BsonDocument>(config.Collection);

    private MongoClient Client()
    {
        var connectionString = SinkSecrets.Resolve(
            config.ConnectionString, config.ConnectionStringFile, configDir, "reporting.mongo connection string");
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        // A cron audit must fail fast and loudly, not hang on a dead DB.
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        settings.ConnectTimeout = TimeSpan.FromSeconds(10);
        return new MongoClient(settings);
    }
}
