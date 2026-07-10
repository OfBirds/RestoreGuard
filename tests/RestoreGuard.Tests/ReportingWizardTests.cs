using System.Text.Json;
using RestoreGuard.Cli;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Tests;

public class ReportingWizardTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rg-reporting-wizard");
    private string ConfigPath => Path.Combine(_dir.FullName, "restoreguard.json");
    private readonly List<string> _probed = [];

    public ReportingWizardTests()
    {
        // A minimal existing config: the wizard must ONLY touch its reporting section.
        var config = new RestoreGuardConfig([new DockerHostConfig("nas")], null, null, 26,
            null, null, null, null, null, SuppressionsFile: "suppressions.json");
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, InteractiveMode.WizardJson));
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private async Task<string> RunAsync(bool probeOk, params string[] answers)
    {
        var output = new StringWriter();
        await ReportingWizard.ConfigureAsync(ConfigPath, new WizardIO(new StringReader(string.Join('\n', answers)), output),
            sink =>
            {
                _probed.Add(sink.Description);
                return Task.FromResult(probeOk ? (true, "write access verified") : (false, "simulated failure"));
            });
        return output.ToString();
    }

    [Fact]
    public async Task AllThreeSinks_Configured_ProbedAndWritten_RestOfConfigPreserved()
    {
        var output = await RunAsync(probeOk: true,
            "y", "/var/lib/rg/reports", "30", "spool",           // folder: on, path, keep 30, connection id
            "y", "http://192.168.1.10:9000", "backups", "rg/",   // s3: on, endpoint, bucket, prefix
            "", "n",                                             // region default, not AWS (path-style)
            "/etc/rg/s3.access", "/etc/rg/s3.secret", "offsite", // both keys from files, connection id
            "y", "", "mongodb://192.168.1.11:27017",             // mongo: on, no file -> inline conn string
            "", "", "mongo-main");                               // database + collection defaults, connection id

        var config = RestoreGuardConfig.Load(ConfigPath);
        Assert.Empty(config.Validate());
        // Untouched sections survive the rewrite.
        Assert.Equal("nas", Assert.Single(config.DockerHosts).Alias);
        Assert.Equal("suppressions.json", config.SuppressionsFile);

        // Destinations live in a SEPARATE reporting.json; the main config only points
        // at it (never inlines them), so the file is a standalone artifact HCC can read.
        Assert.Equal("reporting.json", config.ReportingFile);
        Assert.Null(config.Reporting);
        Assert.True(File.Exists(Path.Combine(_dir.FullName, "reporting.json")));

        var reporting = config.LoadReporting(_dir.FullName, []).Config!;
        // Each destination carries its connection id — stamped into every report's metadata.
        Assert.Equal(new FolderSinkConfig("/var/lib/rg/reports", 30, "spool"), reporting.Folder);
        Assert.Equal(new S3SinkConfig("http://192.168.1.10:9000", "backups", "rg/", "us-east-1",
            ForcePathStyle: true, AccessKeyFile: "/etc/rg/s3.access", SecretKeyFile: "/etc/rg/s3.secret", Id: "offsite"), reporting.S3);
        Assert.Equal(new MongoSinkConfig(ConnectionString: "mongodb://192.168.1.11:27017", Id: "mongo-main"), reporting.Mongo);

        // Every destination was live-probed before being accepted.
        Assert.Equal(3, _probed.Count);
        Assert.Contains("report", output); // summary names the destinations
        Assert.Contains("s3 http://192.168.1.10:9000/backups", output);
    }

    [Fact]
    public async Task DeclineEverything_LeavesNoReportingSection_MentionsDefaultFolder()
    {
        var output = await RunAsync(probeOk: true, "n", "n", "n");

        var config = RestoreGuardConfig.Load(ConfigPath);
        Assert.Null(config.Reporting);
        Assert.Null(config.ReportingFile);
        Assert.False(File.Exists(Path.Combine(_dir.FullName, "reporting.json")));
        Assert.Contains("default folder", output);
        Assert.Empty(_probed);
    }

    [Fact]
    public async Task FailedFolderProbe_DeclinedTwice_SkipsTheSink()
    {
        var output = await RunAsync(probeOk: false,
            "y", "/mnt/dead/reports", "n", "n",  // folder: on, path, don't keep, don't retry
            "n", "n");                           // s3 off, mongo off

        Assert.Null(RestoreGuardConfig.Load(ConfigPath).Reporting);
        Assert.Contains("PROBLEM — simulated failure", output);
        Assert.Contains("Skipping the folder destination", output);
    }

    [Fact]
    public async Task FailedS3Probe_KeepAnyway_IsHonored()
    {
        var output = await RunAsync(probeOk: false,
            "n",                                          // folder off
            "y", "http://h:9000", "", "", "", "n",        // s3 on, endpoint, bucket/prefix/region defaults, not AWS
            "", "minio", "", "secret",                    // keys inline (no files)
            "",                                           // connection id: Enter -> default (s3:restoreguard)
            "y",                                          // keep anyway despite failed probe
            "n");                                         // mongo off

        var s3 = RestoreGuardConfig.Load(ConfigPath).LoadReporting(_dir.FullName, []).Config!.S3!;
        Assert.Equal(("http://h:9000", "restoreguard", "minio", "secret"),
            (s3.Endpoint, s3.Bucket, s3.AccessKey, s3.SecretKey));
        // The id prompt is pre-filled with a sensible default (s3:<bucket>); Enter accepts
        // and stores it, so it's visible in reporting.json for a reader.
        Assert.Equal("s3:restoreguard", s3.Id);
        Assert.Contains("PROBLEM — simulated failure", output);
    }

    [Fact]
    public async Task ReconfiguringReadsBackTheSeparateFile()
    {
        // First run configures a folder; second run must see it as "existing" (loaded
        // from reporting.json, not the main config) and preserve it when only S3 changes.
        await RunAsync(probeOk: true, "y", "/var/lib/rg/reports", "", "spool", "n", "n");
        _probed.Clear();

        await RunAsync(probeOk: true,
            "y", "", "", "",                              // folder: keep path, keepLast, connection id defaults
            "n", "n");                                    // s3 off, mongo off

        var reporting = RestoreGuardConfig.Load(ConfigPath).LoadReporting(_dir.FullName, []).Config!;
        Assert.NotNull(reporting.Folder);
        // The id set on the first run survived being read back and rewritten.
        Assert.Equal("spool", reporting.Folder!.Id);
    }
}
