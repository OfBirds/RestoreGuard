using RestoreGuard.Checks;
using RestoreGuard.Cli;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers;
using RestoreGuard.Providers.Docker;
using RestoreGuard.Providers.FileBackups;

namespace RestoreGuard.Tests;

/// <summary>Serves canned canary-probe responses keyed by the command's shape.</summary>
file sealed class FakeCanarySsh(Func<string, SshResult> respond) : ISshProvider
{
    public List<string> Commands { get; } = [];

    public Task<SshResult> RunAsync(string hostAlias, string command, CancellationToken ct = default)
    {
        Commands.Add(command);
        return Task.FromResult(respond(command));
    }
}

public class RestoreCanaryTests
{
    private static readonly LabInventory EmptyInventory =
        new(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero), [], [], []);

    private static FileBackupSource Restic(string canary = "/opt/monitoring/docker-compose.yml") =>
        new("restic app configs", "restic", "lab98",
            Repo: "/mnt/nas/restic", PasswordFile: "/root/.restic-pass", CanaryPath: canary);

    private static FileBackupSource Borg(string canary = "/etc/fstab") =>
        new("borg system configs", "borg", "lab55",
            Repo: "/var/backups/borg-lab55", PasswordFile: "/root/.borg-pass", CanaryPath: canary);

    [Fact]
    public async Task ResticCanary_CountsBytesOnHost_NothingCrossesTheWire()
    {
        var ssh = new FakeCanarySsh(_ => new SshResult(0, "1234\n", ""));

        var result = await new FileBackupProvider(ssh).ProbeCanaryAsync(Restic());

        Assert.Equal(1234, result.Bytes);
        Assert.Null(result.Detail);
        var cmd = Assert.Single(ssh.Commands);
        // The dump is piped into wc ON the host — the file content must never be
        // shipped back, and 'latest' pins the probe to the newest snapshot.
        Assert.Contains("dump latest '/opt/monitoring/docker-compose.yml' | wc -c", cmd);
        Assert.Contains("--no-lock", cmd);
    }

    [Fact]
    public async Task BorgCanary_ResolvesLatestArchive_AndStripsLeadingSlash()
    {
        var ssh = new FakeCanarySsh(cmd => cmd.Contains("borg list")
            ? new SshResult(0, Fixtures.Read("borg-list.json"), "")
            : new SshResult(0, "42\n", ""));

        var result = await new FileBackupProvider(ssh).ProbeCanaryAsync(Borg("/etc/fstab"));

        Assert.Equal(42, result.Bytes);
        var extract = ssh.Commands[1];
        // Borg has no 'latest' selector (needs the archive name) and stores paths
        // without the leading slash — both must be handled for the user.
        Assert.Contains("::lab55-2026-07-05_0837' 'etc/fstab'", extract);
        Assert.Contains("| wc -c", extract);
    }

    [Fact]
    public async Task FailedRestore_SurfacesAsZeroBytesWithStderr_NotProviderError()
    {
        // The pipeline's exit code is wc's: a failed restic dump exits the pipe with 0,
        // 0 bytes counted, and the error on stderr. That must become a CanaryResult
        // (-> RED finding), not a thrown provider error.
        var ssh = new FakeCanarySsh(_ => new SshResult(0, "0\n", "Fatal: no matching entries found"));

        var result = await new FileBackupProvider(ssh).ProbeCanaryAsync(Restic());

        Assert.Equal(0, result.Bytes);
        Assert.Equal("Fatal: no matching entries found", result.Detail);
    }

    [Fact]
    public async Task CanaryOnUnsupportedKind_IsProviderError()
    {
        var source = new FileBackupSource("k", "kopia", "lab118", CanaryPath: "/etc/fstab");

        await Assert.ThrowsAsync<ProviderException>(
            () => new FileBackupProvider(new FakeCanarySsh(_ => new SshResult(0, "", ""))).ProbeCanaryAsync(source));
    }

    [Fact]
    public void GoldenFile_BorgLatestArchiveName()
    {
        Assert.Equal("lab55-2026-07-05_0837",
            FileBackupProvider.ParseBorgLatestArchive(Fixtures.Read("borg-list.json")));
    }

    [Fact]
    public void RestoredCanary_YieldsNoFinding()
    {
        var check = new RestoreCanaryCheck(
            [new CanaryResult("restic app configs", "lab98", "/opt/x.yml", 1234, null)]);

        Assert.Empty(check.Evaluate(EmptyInventory));
    }

    [Fact]
    public void ZeroByteCanary_IsRed_WithToolStderrAsEvidence()
    {
        var check = new RestoreCanaryCheck(
        [
            new CanaryResult("restic app configs", "lab98", "/opt/x.yml", 0, "Fatal: wrong password"),
            new CanaryResult("borg system configs", "lab55", "/etc/fstab", 0, null),
        ]);

        var findings = check.Evaluate(EmptyInventory).ToList();

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(("restore-canary/failed", Severity.Red), (f.RuleId, f.Severity)));
        Assert.Contains("Fatal: wrong password", findings[0].Evidence);
        Assert.Contains("reported nothing", findings[1].Evidence);
    }

    [Fact]
    public void CanaryPathOnNonRestoreKinds_IsConfigError()
    {
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null,
        [
            new FileBackupSource("d", "dir", "lab118", Path: "/opt/backups", CanaryPath: "member.tar"),
            new FileBackupSource("r", "restic", "lab98", Repo: "/mnt/r", PasswordFile: "/root/.p", CanaryPath: "/etc/fstab"),
        ], null);

        var error = Assert.Single(config.Validate());
        Assert.Contains("canaryPath is only supported for restic and borg", error);
    }
}
