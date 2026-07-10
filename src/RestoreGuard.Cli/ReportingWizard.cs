using System.Text.Json;

namespace RestoreGuard.Cli;

/// <summary>
/// The `r` menu entry: configures where audit reports are delivered (folder,
/// S3-compatible bucket, MongoDB — any combination). Every destination is probed
/// LIVE with the same write the audit will do, so credentials and reachability
/// fail here, not silently at 3 AM in cron. Rewrites only the reporting section;
/// the rest of the config is preserved.
/// </summary>
public static class ReportingWizard
{
    public static Task ConfigureAsync(string configPath, WizardIO io) =>
        ConfigureAsync(configPath, io, VerifySinkAsync);

    internal static async Task ConfigureAsync(
        string configPath, WizardIO io, Func<IReportSink, Task<(bool Ok, string Message)>> probe)
    {
        var config = RestoreGuardConfig.LoadValidated(configPath);
        if (config is null)
            return;
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        // Load whatever is currently configured (from the separate file or inline).
        var existing = config.LoadReporting(configDir, []).Config;
        // The self-contained destinations file the wizard writes and HCC can read.
        const string reportingFileName = "reporting.json";
        var reportingPath = Path.Combine(configDir, reportingFileName);

        io.WriteLine();
        io.WriteLine("--- Report destinations (each audit delivers its JSON report to ALL of these) ---");
        io.WriteLine(existing is null
            ? $"Currently: none configured — reports go to the default folder {ReportPublisher.DefaultFolder()}"
            : "Currently: " + DescribeSinks(existing));
        io.WriteLine($"These go in their own file ({reportingFileName}) so another tool — e.g. HCC —");
        io.WriteLine("can read the same file and pull the reports back. Answer the three sections;");
        io.WriteLine("what you skip is removed.");

        var folder = await ConfigureFolderAsync(io, existing?.Folder, configDir, probe);
        var s3 = await ConfigureS3Async(io, existing?.S3, configDir, probe);
        var mongo = await ConfigureMongoAsync(io, existing?.Mongo, configDir, probe);

        var reporting = folder is null && s3 is null && mongo is null
            ? null
            : new ReportingConfig(folder, s3, mongo);

        // The main config only POINTS at the destinations file (never inlines them),
        // so the destinations file stays a standalone, shareable artifact.
        if (reporting is null)
        {
            if (File.Exists(reportingPath))
                File.Delete(reportingPath);
            File.WriteAllText(configPath, JsonSerializer.Serialize(
                config with { ReportingFile = null, Reporting = null }, InteractiveMode.WizardJson));
        }
        else
        {
            File.WriteAllText(reportingPath, JsonSerializer.Serialize(reporting, InteractiveMode.WizardJson));
            File.WriteAllText(configPath, JsonSerializer.Serialize(
                config with { ReportingFile = reportingFileName, Reporting = null }, InteractiveMode.WizardJson));
        }

        io.WriteLine();
        io.WriteLine(reporting is null
            ? $"No destinations configured — reports go to the default folder {ReportPublisher.DefaultFolder()}."
            : $"Reports now go to: {DescribeSinks(reporting)}.");
        io.WriteLine(reporting is null
            ? $"Wrote {configPath} (removed the reporting pointer)."
            : $"Wrote {reportingPath} (the destinations) and pointed {Path.GetFileName(configPath)} at it via");
        if (reporting is not null)
        {
            io.WriteLine($"\"reportingFile\": \"{reportingFileName}\". Hand that same file to HCC to read reports back;");
            io.WriteLine("a folder destination always keeps a stable latest.json for easy consumption.");
        }
    }

    private static string DescribeSinks(ReportingConfig r)
    {
        var parts = new List<string>();
        if (r.Folder is { } f) parts.Add($"folder {(string.IsNullOrWhiteSpace(f.Path) ? ReportPublisher.DefaultFolder() : f.Path)}");
        if (r.S3 is { } s) parts.Add($"s3 {s.Endpoint.TrimEnd('/')}/{s.Bucket}");
        if (r.Mongo is { } m) parts.Add($"mongo {m.Database}.{m.Collection}");
        return string.Join(", ", parts);
    }

    private static async Task<FolderSinkConfig?> ConfigureFolderAsync(
        WizardIO io, FolderSinkConfig? existing, string configDir,
        Func<IReportSink, Task<(bool Ok, string Message)>> probe)
    {
        io.WriteLine();
        if (!InteractiveMode.AskYesNo(io, $"Save reports to a folder?{(existing is null ? "" : " (currently on)")}"))
            return null;

        while (true)
        {
            var path = InteractiveMode.Ask(io,
                $"  folder path (Enter = {ReportPublisher.DefaultFolder()})", existing?.Path ?? "");
            var resolved = ReportPublisher.ResolveFolder(path.Length == 0 ? null : path, configDir);

            io.Write("  checking write access ... ");
            var (ok, message) = await probe(new FolderReportSink(resolved, keepLast: null));
            io.WriteLine(ok ? $"OK — {message}" : $"PROBLEM — {message}");
            if (!ok && !InteractiveMode.AskYesNo(io, "  Keep this folder anyway?"))
            {
                if (InteractiveMode.AskYesNo(io, "  Try a different folder?"))
                    continue;
                io.WriteLine("  Skipping the folder destination.");
                return null;
            }

            var keepLast = AskOptionalCount(io,
                "  keep only the newest N reports there (Enter = keep all)", existing?.KeepLast);
            return new FolderSinkConfig(path.Length == 0 ? null : path, keepLast);
        }
    }

    private static async Task<S3SinkConfig?> ConfigureS3Async(
        WizardIO io, S3SinkConfig? existing, string configDir,
        Func<IReportSink, Task<(bool Ok, string Message)>> probe)
    {
        io.WriteLine();
        if (!InteractiveMode.AskYesNo(io,
                $"Upload reports to S3-compatible object storage (MinIO, Garage, R2, AWS)?{(existing is null ? "" : " (currently on)")}"))
            return null;

        while (true)
        {
            var endpoint = InteractiveMode.Ask(io,
                "  endpoint URL (e.g. http://192.168.1.10:9000; Enter = cancel)", existing?.Endpoint ?? "");
            if (endpoint.Length == 0)
            {
                io.WriteLine("  Skipping the S3 destination.");
                return null;
            }
            var bucket = InteractiveMode.Ask(io, "  bucket (must already exist)", existing?.Bucket ?? "restoreguard");
            var prefix = InteractiveMode.Ask(io, "  key prefix inside the bucket", existing?.Prefix ?? "restoreguard/");
            var region = InteractiveMode.Ask(io, "  region (S3-compatible servers accept the default)", existing?.Region ?? "us-east-1");
            // Path-style is what self-hosted stores expect; only AWS itself wants
            // bucket-in-hostname addressing.
            var pathStyle = !InteractiveMode.AskYesNo(io, "  is this AWS S3 itself (virtual-host addressing)?");

            var (accessKey, accessKeyFile) = AskSecret(io, "access key", existing?.AccessKey, existing?.AccessKeyFile);
            if (accessKey is null && accessKeyFile is null)
            {
                io.WriteLine("  Skipping the S3 destination (no access key).");
                return null;
            }
            var (secretKey, secretKeyFile) = AskSecret(io, "secret key", existing?.SecretKey, existing?.SecretKeyFile);
            if (secretKey is null && secretKeyFile is null)
            {
                io.WriteLine("  Skipping the S3 destination (no secret key).");
                return null;
            }

            var candidate = new S3SinkConfig(endpoint, bucket, prefix, region, pathStyle,
                accessKey, accessKeyFile, secretKey, secretKeyFile);

            io.Write("  checking (writing + removing a test object) ... ");
            var (ok, message) = await probe(new S3ReportSink(candidate, configDir));
            io.WriteLine(ok ? $"OK — {message}" : $"PROBLEM — {message}");
            if (ok || InteractiveMode.AskYesNo(io, "  Keep this destination anyway?"))
                return candidate;
            if (!InteractiveMode.AskYesNo(io, "  Re-enter the S3 settings?"))
            {
                io.WriteLine("  Skipping the S3 destination.");
                return null;
            }
            existing = candidate; // re-ask with the rejected answers as defaults
        }
    }

    private static async Task<MongoSinkConfig?> ConfigureMongoAsync(
        WizardIO io, MongoSinkConfig? existing, string configDir,
        Func<IReportSink, Task<(bool Ok, string Message)>> probe)
    {
        io.WriteLine();
        if (!InteractiveMode.AskYesNo(io,
                $"Insert reports into MongoDB?{(existing is null ? "" : " (currently on)")}"))
            return null;

        while (true)
        {
            var (connectionString, connectionStringFile) = AskSecret(io,
                "connection string (mongodb://user:pass@host:27017)",
                existing?.ConnectionString, existing?.ConnectionStringFile);
            if (connectionString is null && connectionStringFile is null)
            {
                io.WriteLine("  Skipping the MongoDB destination (no connection string).");
                return null;
            }
            var database = InteractiveMode.Ask(io, "  database", existing?.Database ?? "restoreguard");
            var collection = InteractiveMode.Ask(io, "  collection", existing?.Collection ?? "reports");

            var candidate = new MongoSinkConfig(connectionString, connectionStringFile, database, collection);

            io.Write("  checking (ping) ... ");
            var (ok, message) = await probe(new MongoReportSink(candidate, configDir));
            io.WriteLine(ok ? $"OK — {message}" : $"PROBLEM — {message}");
            if (ok || InteractiveMode.AskYesNo(io, "  Keep this destination anyway?"))
                return candidate;
            if (!InteractiveMode.AskYesNo(io, "  Re-enter the MongoDB settings?"))
            {
                io.WriteLine("  Skipping the MongoDB destination.");
                return null;
            }
            existing = candidate;
        }
    }

    /// <summary>A secret is better in a root-only file (the passwordFile pattern),
    /// so the file is offered first; Enter falls through to typing it inline.
    /// Returns (inline, file) — at most one set; both null = user gave nothing.</summary>
    private static (string? Inline, string? File) AskSecret(
        WizardIO io, string what, string? existingInline, string? existingFile)
    {
        var file = InteractiveMode.Ask(io,
            $"  file holding the {what} (recommended; Enter = type it inline)", existingFile ?? "");
        if (file.Length > 0)
            return (null, file);
        var inline = InteractiveMode.Ask(io, $"  {what} (stored in the config file; Enter = cancel)", existingInline ?? "");
        return inline.Length > 0 ? (inline, null) : (null, null);
    }

    private static int? AskOptionalCount(WizardIO io, string prompt, int? existing)
    {
        while (true)
        {
            var answer = InteractiveMode.Ask(io, prompt, existing?.ToString() ?? "");
            if (answer.Length == 0)
                return null;
            if (int.TryParse(answer, out var n) && n > 0)
                return n;
            io.WriteLine($"  PROBLEM — '{answer}' is not a positive number (Enter = keep all).");
        }
    }

    private static async Task<(bool Ok, string Message)> VerifySinkAsync(IReportSink sink)
    {
        try
        {
            await sink.VerifyAsync();
            return (true, "write access verified");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
