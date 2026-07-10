namespace RestoreGuard.Cli;

/// <summary>
/// Turns the reporting config into sinks and delivers a finished report to all of
/// them in parallel. A sink failure never eats the report (stdout already has it)
/// but it is loud: a FAIL line per sink and a non-zero failure count, which the
/// audit folds into exit code 1 — a report that silently went nowhere would blind
/// every downstream consumer.
/// </summary>
public static class ReportPublisher
{
    /// <summary>Overrides the no-config default folder — used by tests and
    /// containers where the per-user folder makes no sense.</summary>
    public const string ReportsDirEnvVar = "RESTOREGUARD_REPORTS_DIR";

    public static string DefaultFolder()
    {
        if (Environment.GetEnvironmentVariable(ReportsDirEnvVar) is { Length: > 0 } overridden)
            return overridden;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RestoreGuard", "reports");
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataHome, "restoreguard", "reports");
    }

    /// <summary>The configured sinks; with none configured, the default folder —
    /// an audit always persists its report somewhere predictable.</summary>
    /// <summary>Resolves the reporting config (separate reportingFile or inline) and
    /// builds its sinks — the entry point audit and doctor use.</summary>
    public static IReadOnlyList<IReportSink> BuildSinks(RestoreGuardConfig config, string configDir)
    {
        var resolved = config.LoadReporting(configDir, []);
        return BuildSinks(resolved.Config, resolved.SecretsBaseDir);
    }

    /// <summary>Builds the sinks for an already-resolved reporting config. secretsBaseDir
    /// is where each sink's <c>*File</c> secret and relative folder path resolve from —
    /// the reporting file's own directory, so the same file works for HCC.</summary>
    public static IReadOnlyList<IReportSink> BuildSinks(ReportingConfig? reporting, string secretsBaseDir)
    {
        var sinks = new List<IReportSink>();
        if (reporting?.Folder is { } folder)
            sinks.Add(new FolderReportSink(ResolveFolder(folder.Path, secretsBaseDir), folder.KeepLast, folder.Id));
        if (reporting?.S3 is { } s3)
            sinks.Add(new S3ReportSink(s3, secretsBaseDir));
        if (reporting?.Mongo is { } mongo)
            sinks.Add(new MongoReportSink(mongo, secretsBaseDir));
        if (sinks.Count == 0)
            sinks.Add(new FolderReportSink(DefaultFolder(), keepLast: null));
        return sinks;
    }

    public static string ResolveFolder(string? path, string configDir) =>
        string.IsNullOrWhiteSpace(path) ? DefaultFolder()
        : Path.IsPathRooted(path) ? path
        : Path.Combine(configDir, path);

    /// <summary>Publishes to every sink in parallel; narrates one line per sink
    /// via <paramref name="progress"/> and returns how many failed.</summary>
    public static async Task<int> PublishAsync(
        IReadOnlyList<IReportSink> sinks, string reportJson, DateTimeOffset generatedAt, Action<string> progress)
    {
        var results = await Task.WhenAll(sinks.Select(async sink =>
        {
            try
            {
                return (sink, Detail: await sink.PublishAsync(reportJson, generatedAt), Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (sink, Detail: "", Error: ex.Message);
            }
        }));

        var failures = 0;
        foreach (var (sink, detail, error) in results)
        {
            if (error is null)
            {
                progress($"report ok    {sink.Description}: {detail}");
            }
            else
            {
                failures++;
                progress($"report FAIL  {sink.Description}: {error}");
            }
        }
        return failures;
    }
}
