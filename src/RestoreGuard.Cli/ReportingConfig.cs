namespace RestoreGuard.Cli;

/// <summary>The reporting config plus the directory its <c>*File</c> secrets resolve
/// against (the reporting file's own directory when loaded from reportingFile, so the
/// file is self-contained and portable to another tool like HCC).</summary>
public sealed record ResolvedReporting(ReportingConfig? Config, string SecretsBaseDir);

/// <summary>
/// Where audit reports go. Every sink is optional and they all run in parallel;
/// with no sink configured the report lands in the per-user default folder
/// (Documents\RestoreGuard\reports on Windows, ~/.local/share/restoreguard/reports
/// elsewhere) so a report always exists somewhere a script can pick it up.
///
/// This is the shape of the standalone reporting file (reporting.json): it is
/// self-contained so a second tool — e.g. HCC — can read the SAME file, connect to
/// the SAME destinations, and pull the reports RestoreGuard wrote there.
/// </summary>
public sealed record ReportingConfig(
    FolderSinkConfig? Folder = null,
    S3SinkConfig? S3 = null,
    MongoSinkConfig? Mongo = null)
{
    public void Validate(List<string> errors)
    {
        if (Folder is { KeepLast: <= 0 })
            errors.Add("reporting.folder.keepLast must be a positive number of reports to keep (omit it to keep all).");

        if (S3 is { } s3)
        {
            if (!Uri.TryCreate(s3.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                errors.Add("reporting.s3.endpoint must be an http(s) URL (e.g. http://192.168.1.10:9000).");
            if (string.IsNullOrWhiteSpace(s3.Bucket))
                errors.Add("reporting.s3.bucket is empty.");
            RequireExactlyOne(errors, "reporting.s3", "accessKey", s3.AccessKey, s3.AccessKeyFile);
            RequireExactlyOne(errors, "reporting.s3", "secretKey", s3.SecretKey, s3.SecretKeyFile);
        }

        if (Mongo is { } mongo)
            RequireExactlyOne(errors, "reporting.mongo", "connectionString", mongo.ConnectionString, mongo.ConnectionStringFile);

        var ids = EffectiveIds().ToList();
        foreach (var dup in ids.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key))
            errors.Add($"reporting: duplicate connection id '{dup}' — each destination needs a unique id.");
    }

    /// <summary>The connection id of each configured destination (explicit or derived).</summary>
    public IEnumerable<string> EffectiveIds()
    {
        if (Folder is { } f) yield return f.Id ?? "folder";
        if (S3 is { } s) yield return s.Id ?? $"s3:{s.Bucket}";
        if (Mongo is { } m) yield return m.Id ?? $"mongo:{m.Database}.{m.Collection}";
    }

    private static void RequireExactlyOne(List<string> errors, string section, string field, string? inline, string? file)
    {
        var hasInline = !string.IsNullOrWhiteSpace(inline);
        var hasFile = !string.IsNullOrWhiteSpace(file);
        if (hasInline == hasFile)
            errors.Add(hasInline
                ? $"{section}.{field} and {section}.{field}File are both set — pick one."
                : $"{section} needs {field} or {field}File.");
    }
}

/// <summary>A local (or mounted) directory: rg-report-&lt;utc-ts&gt;.json + latest.json.
/// Relative path resolves against the reporting file's directory. Id defaults to "folder".</summary>
public sealed record FolderSinkConfig(
    string? Path = null,
    int? KeepLast = null,
    string? Id = null,
    bool Enabled = true);

/// <summary>Any S3-compatible store (MinIO, Garage, R2, AWS). ForcePathStyle=false for
/// AWS virtual-host addressing. Secrets inline or via *File. Id defaults to "s3:&lt;bucket&gt;".</summary>
public sealed record S3SinkConfig(
    string Endpoint,
    string Bucket,
    string Prefix = "restoreguard/",
    string Region = "us-east-1",
    bool ForcePathStyle = true,
    string? AccessKey = null,
    string? AccessKeyFile = null,
    string? SecretKey = null,
    string? SecretKeyFile = null,
    string? Id = null,
    bool Enabled = true);

/// <summary>A MongoDB collection (one document per report). Id defaults to
/// "mongo:&lt;database&gt;.&lt;collection&gt;".</summary>
public sealed record MongoSinkConfig(
    string? ConnectionString = null,
    string? ConnectionStringFile = null,
    string Database = "restoreguard",
    string Collection = "reports",
    string? Id = null,
    bool Enabled = true);
