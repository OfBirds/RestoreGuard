using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Offsite;

namespace RestoreGuard.Tests;

public class PbsOffsiteTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly PbsOffsiteConfig Config =
        new("lab99", "/var/log/pbs-onedrive-sync.log", "onedrive:", "pbs-nvme");

    [Fact]
    public void GoldenFile_LogParsesLastRunAsSuccess()
    {
        // The captured tail contains the real 2026-07-03 TLS failure (rc=1) followed
        // by the recovered 2026-07-04 run (rc=0) — the parser must report the latest.
        var run = PbsOffsiteProvider.ParseLastRun(Fixtures.Read("pbs-sync-log-tail.txt"));

        Assert.NotNull(run);
        Assert.Equal(0, run.Value.Rc);
        Assert.Equal(new DateOnly(2026, 7, 4), DateOnly.FromDateTime(run.Value.Start.UtcDateTime));
    }

    [Fact]
    public void FailedRunAndUnfinishedRunReportNonZeroRc()
    {
        var failed = PbsOffsiteProvider.ParseLastRun(
            "=== 2026-07-03 05:00 CEST sync start ===\n=== sync finished rc=1 ===\n");
        Assert.Equal(1, failed!.Value.Rc);

        // A start with no finish line = killed mid-run; must not count as success.
        var unfinished = PbsOffsiteProvider.ParseLastRun(
            "=== 2026-07-03 05:00 CEST sync start ===\n2026/07/03 05:01:00 NOTICE: transferring\n");
        Assert.Equal(-1, unfinished!.Value.Rc);

        Assert.Null(PbsOffsiteProvider.ParseLastRun("no sync lines here"));
    }

    [Fact]
    public void GoldenFile_RcloneAboutBecomesStorageTarget()
    {
        var target = PbsOffsiteProvider.ParseAbout(Fixtures.Read("rclone-about.json"), Config);

        Assert.Equal("onedrive:", target.Name);
        Assert.Equal("lab99", target.Host);
        Assert.Equal(1104880336896, target.CapacityBytes);
        Assert.Equal(1015545017139, target.FreeBytes);
        // ~8% used — well under the 80% warn threshold StorageCapacityCheck applies.
    }

    [Fact]
    public void FailedSyncIsRed_StaleSyncIsRed()
    {
        var check = new PbsOffsiteCheck(new PbsOffsiteOptions("lab99", TimeSpan.FromHours(26)));

        BackupArtifact Sync(DateTimeOffset ts, string status) => new(
            BackupTier.CloudSync, "pbs-nvme", "onedrive:", ts, 0, "rclone-pbs-offsite", true, status);

        var failed = Assert.Single(check.Evaluate(new LabInventory(Now, [], [Sync(Now.AddHours(-7), "failed")], [])));
        Assert.Equal(("pbs/offsite-failed", Severity.Red), (failed.RuleId, failed.Severity));

        var stale = Assert.Single(check.Evaluate(new LabInventory(Now, [], [Sync(Now.AddDays(-3), "ok")], [])));
        Assert.Equal(("pbs/offsite-stale", Severity.Red), (stale.RuleId, stale.Severity));

        Assert.Empty(check.Evaluate(new LabInventory(Now, [], [Sync(Now.AddHours(-7), "ok")], [])));
    }
}
