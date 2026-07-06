using RestoreGuard.Checks;
using RestoreGuard.Cli;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers;
using RestoreGuard.Providers.Offsite;

namespace RestoreGuard.Tests;

file sealed class FakeOffsiteSsh(Func<string, SshResult> respond) : ISshProvider
{
    public List<string> Commands { get; } = [];

    public Task<SshResult> RunAsync(string hostAlias, string command, CancellationToken ct = default)
    {
        Commands.Add(command);
        return Task.FromResult(respond(command));
    }
}

public class OffsiteJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 23, 0, 0, TimeSpan.Zero);

    private static OffsiteJobExpectation Expect(string name = "onedrive push") =>
        new(name, "lab99", TimeSpan.FromHours(26));

    private static BackupArtifact Sync(string name, DateTimeOffset ts, string status) =>
        new(BackupTier.CloudSync, name, $"onedrive: ({name} on lab99)", ts, 0,
            "rclone-offsite", HasOffsiteCopy: true, Status: status);

    // ---------- provider ----------

    [Fact]
    public async Task JobWithRemote_TailsLogAndAsksAbout()
    {
        var ssh = new FakeOffsiteSsh(cmd => cmd.StartsWith("tail")
            ? new SshResult(0, Fixtures.Read("pbs-sync-log-tail.txt"), "")
            : new SshResult(0, """{"total":100,"free":40}""", ""));

        var state = await new PbsOffsiteProvider(ssh).GetJobAsync(
            new OffsiteJobConfig("onedrive push", "lab99", "/var/log/pbs-onedrive-sync.log", "onedrive:"));

        Assert.Equal(2, ssh.Commands.Count);
        Assert.Contains("tail -n 200 '/var/log/pbs-onedrive-sync.log'", ssh.Commands[0]);
        Assert.Contains("rclone about onedrive: --json", ssh.Commands[1]);
        // The fixture's last run is 2026-07-04 05:00 with rc=0 (the rc=1 run before it must not win).
        Assert.Equal(("onedrive push", "rclone-offsite", "ok"),
            (state.LastSync!.TargetService, state.LastSync.Method, state.LastSync.Status));
        Assert.Equal(new DateOnly(2026, 7, 4), DateOnly.FromDateTime(state.LastSync.Timestamp.UtcDateTime));
        Assert.Equal((100, 40), (state.Remote!.CapacityBytes, state.Remote.FreeBytes));
    }

    [Fact]
    public async Task JobWithoutRemote_NeverRunsRclone()
    {
        var ssh = new FakeOffsiteSsh(_ => new SshResult(0, "", ""));

        var state = await new PbsOffsiteProvider(ssh).GetJobAsync(
            new OffsiteJobConfig("usb copy", "lab55", "/var/log/usb-sync.log"));

        Assert.Single(ssh.Commands);
        Assert.Null(state.Remote);
        Assert.Null(state.LastSync); // empty log: no runs — the check's never-ran case
    }

    // ---------- check ----------

    [Fact]
    public void JobWithNoRunsAtAll_IsRedNeverRan()
    {
        var check = new OffsiteJobCheck([Expect()]);

        var finding = Assert.Single(check.Evaluate(new LabInventory(Now, [], [], [])));
        Assert.Equal(("offsite/never-ran", Severity.Red), (finding.RuleId, finding.Severity));
        Assert.Contains("sync start", finding.SuggestedAction); // explains the log contract
    }

    [Fact]
    public void FailedAndStaleRuns_AreRed_FreshOkIsQuiet()
    {
        var check = new OffsiteJobCheck([Expect("failed job"), Expect("stale job"), Expect("good job")]);

        var findings = check.Evaluate(new LabInventory(Now, [],
        [
            Sync("failed job", Now.AddHours(-1), "failed"),
            Sync("stale job", Now.AddDays(-4), "ok"),
            Sync("good job", Now.AddHours(-2), "ok"),
        ], [])).ToList();

        Assert.Equal(2, findings.Count);
        Assert.Equal("offsite/failed", Assert.Single(findings, f => f.Service == "failed job").RuleId);
        Assert.Equal("offsite/stale", Assert.Single(findings, f => f.Service == "stale job").RuleId);
    }

    [Fact]
    public void LegacyPbsOffsiteArtifacts_AreNotThisChecksBusiness()
    {
        var legacy = new BackupArtifact(BackupTier.CloudSync, "onedrive push", "x", Now.AddDays(-9), 0,
            "rclone-pbs-offsite", true, "failed");

        var check = new OffsiteJobCheck([Expect()]);

        // The legacy artifact must not satisfy (or trip) the generic expectation:
        // the job still reads as never-ran.
        var finding = Assert.Single(check.Evaluate(new LabInventory(Now, [], [legacy], [])));
        Assert.Equal("offsite/never-ran", finding.RuleId);
    }

    // ---------- config validation ----------

    [Fact]
    public void EmptyFieldsAreCaughtWithFieldNames()
    {
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null,
            OffsiteJobs: [new OffsiteJobConfig("", "", "")]);

        var errors = config.Validate();

        Assert.Contains("offsiteJobs[0].name is empty.", errors);
        Assert.Contains("offsiteJobs[0].alias is empty.", errors);
        Assert.Contains("offsiteJobs[0].logPath is empty.", errors);
    }
}
