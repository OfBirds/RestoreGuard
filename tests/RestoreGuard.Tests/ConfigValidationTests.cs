using RestoreGuard.Cli;
using RestoreGuard.Providers.Docker;
using RestoreGuard.Providers.FileBackups;
using RestoreGuard.Providers.Pve;

namespace RestoreGuard.Tests;

public class ConfigValidationTests
{
    private static RestoreGuardConfig Minimal() =>
        new([], null, null, 26, null, null, null, null, null, null);

    [Fact]
    public void EmptyConfigIsValid() => Assert.Empty(Minimal().Validate());

    [Fact]
    public void EmptyHostsAreCaughtWithFieldNames()
    {
        // The exact broken config the v0.1.0 wizard could write: y to dumps, empty host.
        var config = Minimal() with
        {
            DockerHosts = [new DockerHostConfig("")],
            LogicalDbBackups = [new LogicalDbBackupConfig("", "/var/backups/db-prod", [""])],
            PveNodes = [new PveNodeConfig("", "")],
        };

        var errors = config.Validate();

        Assert.Contains("dockerHosts[0].alias is empty.", errors);
        Assert.Contains("logicalDbBackups[0].host is empty.", errors);
        Assert.Contains("pveNodes[0].alias is empty.", errors);
        Assert.Contains("pveNodes[0].node is empty.", errors);
    }

    [Fact]
    public void FileBackupKindRequirementsAreChecked()
    {
        var config = Minimal() with
        {
            FileBackups =
            [
                new FileBackupSource("r", "restic", "lab98"),          // missing repo+passwordFile
                new FileBackupSource("b", "borg", "lab55"),            // missing repo+passwordFile
                new FileBackupSource("d", "dir", "lab118"),            // missing path
                new FileBackupSource("h", "haos", "lab99"),            // missing vmid
                new FileBackupSource("s", "snapper", "lab55"),         // missing snapperConfig
                new FileBackupSource("x", "carrier-pigeon", "lab99"),  // unknown kind
            ],
        };

        var errors = config.Validate();

        Assert.Equal(6, errors.Count);
        Assert.Contains(errors, e => e.Contains("snapperConfig"));
        Assert.Contains(errors, e => e.Contains("restic"));
        Assert.Contains(errors, e => e.Contains("borg"));
        Assert.Contains(errors, e => e.Contains("needs path"));
        Assert.Contains(errors, e => e.Contains("needs vmid"));
        Assert.Contains(errors, e => e.Contains("carrier-pigeon"));
    }

    [Fact]
    public void ValidRealShapedConfigPasses()
    {
        var config = Minimal() with
        {
            DockerHosts = [new DockerHostConfig("root@192.168.1.10")],
            LogicalDbBackups = [new LogicalDbBackupConfig("root@192.168.1.10", "/var/backups/db-prod", ["root@192.168.1.10"])],
            PveNodes = [new PveNodeConfig("root@192.168.1.5", "pve", ["pbs-store"])],
            FileBackups = [new FileBackupSource("repo", "restic", "root@192.168.1.10", Repo: "/mnt/r", PasswordFile: "/root/.p")],
        };

        Assert.Empty(config.Validate());
    }

    [Fact]
    public void ObjectStorageValidationCoversCredentialsBucketAndBounds()
    {
        var config = Minimal() with
        {
            ObjectStorageBuckets =
            [
                new("", "not-a-url", "Bad_Bucket", Region: "", CheckObjectLock: false,
                    ObjectLockRequired: true, MinRetentionDays: 0,
                    CheckNewestObject: true, MaxNewestObjectAgeHours: 0,
                    MaxListKeys: 0, MaxListRequests: 0),
                new("offsite", "https://user:secret@s3.example.com", "valid-bucket", Region: "us-east-1"),
                new("path", "https://s3.example.com/bucket", "valid-bucket", Region: "us-east-1"),
            ],
        };

        var errors = config.Validate();

        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[0].name is empty"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[0].bucket"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[0].endpoint"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[0].region is empty"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[0] needs accessKey"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[0] needs secretKey"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[0].objectLockRequired"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[0].minRetentionDays requires checkObjectLock"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[0].minRetentionDays must be positive"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[0].maxNewestObjectAgeHours"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[0].maxListKeys"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[0].maxListRequests"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[1].endpoint must not contain credentials"));
        Assert.Contains(errors, e => e.Contains("objectStorageBuckets[2].endpoint must be an origin URL"));
    }

    [Fact]
    public void ValidObjectStorageConfigPasses()
    {
        var config = Minimal() with
        {
            ObjectStorageBuckets = [new(
                "offsite", "https://s3.example.com", "valid-bucket", Prefix: "backup/",
                AccessKeyFile: "s3-access", SecretKeyFile: "s3-secret",
                CheckObjectLock: true, MinRetentionDays: 30,
                CheckNewestObject: true, MaxNewestObjectAgeHours: 26)],
        };

        Assert.Empty(config.Validate());
    }

    [Fact]
    public void ObjectStorageOnlyJson_NormalizesOmittedDockerHosts()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                { "objectStorageBuckets": [{
                  "name": "offsite", "endpoint": "https://s3.example.com", "bucket": "valid-bucket",
                  "accessKeyFile": "s3-access", "secretKeyFile": "s3-secret"
                }] }
                """);

            Assert.Empty(RestoreGuardConfig.Load(path).Validate());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
