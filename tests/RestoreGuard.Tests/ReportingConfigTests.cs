using RestoreGuard.Cli;

namespace RestoreGuard.Tests;

/// <summary>Tests that mutate the RESTOREGUARD_REPORTS_DIR environment variable
/// share this collection so they never race each other (env is process-wide).</summary>
[CollectionDefinition("reports-env")]
public class ReportsEnvCollection;

public class ReportingConfigValidationTests
{
    private static IReadOnlyList<string> Validate(ReportingConfig reporting) =>
        new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null, Reporting: reporting).Validate();

    [Fact]
    public void ValidSinks_NoErrors()
    {
        Assert.Empty(Validate(new ReportingConfig(
            new FolderSinkConfig("/var/lib/restoreguard/reports", KeepLast: 30),
            new S3SinkConfig("http://192.168.1.10:9000", "backups", AccessKey: "k", SecretKeyFile: "/etc/restoreguard/s3.secret"),
            new MongoSinkConfig(ConnectionStringFile: "/etc/restoreguard/mongo.uri"))));
    }

    [Fact]
    public void S3_MissingPieces_EachNamed()
    {
        var errors = Validate(new ReportingConfig(S3: new S3SinkConfig("not a url", "")));

        Assert.Contains(errors, e => e.Contains("reporting.s3.endpoint"));
        Assert.Contains(errors, e => e.Contains("reporting.s3.bucket"));
        Assert.Contains(errors, e => e.Contains("accessKey or accessKeyFile"));
        Assert.Contains(errors, e => e.Contains("secretKey or secretKeyFile"));
    }

    [Fact]
    public void S3_BothInlineAndFileSecret_Rejected()
    {
        var errors = Validate(new ReportingConfig(S3: new S3SinkConfig(
            "http://h:9000", "b", AccessKey: "k", AccessKeyFile: "f", SecretKey: "s")));

        Assert.Contains(errors, e => e.Contains("reporting.s3.accessKey and reporting.s3.accessKeyFile are both set"));
    }

    [Fact]
    public void Mongo_NeedsConnectionString()
    {
        var errors = Validate(new ReportingConfig(Mongo: new MongoSinkConfig()));
        Assert.Contains(errors, e => e.Contains("connectionString or connectionStringFile"));
    }

    [Fact]
    public void Folder_KeepLastMustBePositive()
    {
        var errors = Validate(new ReportingConfig(Folder: new FolderSinkConfig(KeepLast: 0)));
        Assert.Contains(errors, e => e.Contains("reporting.folder.keepLast"));
    }

    [Fact]
    public void ResolveFolder_RelativeAgainstConfigDir_AbsoluteUntouched()
    {
        var configDir = OperatingSystem.IsWindows() ? @"C:\etc\rg" : "/etc/rg";
        Assert.Equal(Path.Combine(configDir, "reports"), ReportPublisher.ResolveFolder("reports", configDir));
        var absolute = OperatingSystem.IsWindows() ? @"D:\reports" : "/var/reports";
        Assert.Equal(absolute, ReportPublisher.ResolveFolder(absolute, configDir));
    }
}

[Collection("reports-env")]
public class DefaultFolderTests
{
    [Fact]
    public void EnvVarOverridesTheDefault_AndEmptyMeansPlatformDefault()
    {
        var original = Environment.GetEnvironmentVariable(ReportPublisher.ReportsDirEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ReportPublisher.ReportsDirEnvVar, @"X:\spool");
            Assert.Equal(@"X:\spool", ReportPublisher.DefaultFolder());

            Environment.SetEnvironmentVariable(ReportPublisher.ReportsDirEnvVar, null);
            var fallback = ReportPublisher.DefaultFolder();
            Assert.EndsWith(OperatingSystem.IsWindows()
                ? Path.Combine("RestoreGuard", "reports")
                : Path.Combine("restoreguard", "reports"), fallback);
            Assert.True(Path.IsPathRooted(fallback));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReportPublisher.ReportsDirEnvVar, original);
        }
    }

    [Fact]
    public void NoSinksConfigured_FallsBackToDefaultFolderSink()
    {
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null);
        var sink = Assert.Single(ReportPublisher.BuildSinks(config, "."));
        Assert.Contains(ReportPublisher.DefaultFolder(), sink.Description);
    }

    [Fact]
    public void ConfiguredSinksOnly_NoDefaultFolderSneaksIn()
    {
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null) with
        {
            Reporting = new ReportingConfig(
                S3: new S3SinkConfig("http://h:9000", "b", AccessKey: "k", SecretKey: "s"),
                Mongo: new MongoSinkConfig(ConnectionString: "mongodb://h")),
        };

        var sinks = ReportPublisher.BuildSinks(config, ".");
        Assert.Equal(2, sinks.Count);
        Assert.DoesNotContain(sinks, s => s.Description.StartsWith("folder"));
    }
}
