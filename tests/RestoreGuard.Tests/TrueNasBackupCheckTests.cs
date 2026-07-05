using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Tests;

public class TrueNasBackupCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly TrueNasBackupCheck Check = new(new TrueNasBackupOptions(
        "truenas", TimeSpan.FromHours(26), TimeSpan.FromHours(26)));

    private static BackupArtifact Snapshot(string dataset, DateTimeOffset ts) =>
        new(BackupTier.ZfsSnapshot, dataset, $"{dataset}@auto", ts, 0, "zfs-auto-snapshot", false, "ok");

    private static BackupArtifact Sync(string dataset, DateTimeOffset ts, string status = "ok") =>
        new(BackupTier.CloudSync, dataset, $"cloudsync task: {dataset}", ts, 0, "rclone-push", true, status);

    private static StorageTarget Dataset(string name) =>
        new(name, "truenas", 1000, 900, "available", null);

    [Fact]
    public void FreshSnapshotAndSyncAreClean()
    {
        var inventory = new LabInventory(Now,
            [], [Snapshot("ssd_pool_one/files", Now.AddHours(-12)), Sync("ssd_pool_one/files", Now.AddHours(-9))],
            [Dataset("ssd_pool_one/files")]);

        Assert.Empty(Check.Evaluate(inventory));
    }

    [Fact]
    public void StaleSnapshotIsRed()
    {
        var inventory = new LabInventory(Now,
            [], [Snapshot("ssd_pool_one/files", Now.AddDays(-3))], []);

        var finding = Assert.Single(Check.Evaluate(inventory));
        Assert.Equal(("zfs/snapshot-stale", Severity.Red), (finding.RuleId, finding.Severity));
    }

    [Fact]
    public void FailedDisabledAndStaleSyncsAreFlagged()
    {
        var inventory = new LabInventory(Now, [],
            [
                Sync("ssd_pool_one/files", Now.AddHours(-3), status: "failed"),
                Sync("ssd_pool_one/backups", Now.AddHours(-3), status: "disabled"),
                Sync("ssd_pool_one/misc", Now.AddDays(-5)),
            ], []);

        var findings = Check.Evaluate(inventory).ToList();

        Assert.Equal(3, findings.Count);
        Assert.Equal(("cloudsync/failed", Severity.Red),
            (findings[0].RuleId, findings[0].Severity));
        Assert.Equal(("cloudsync/disabled", Severity.Yellow),
            (findings[1].RuleId, findings[1].Severity));
        Assert.Equal(("cloudsync/stale", Severity.Red),
            (findings[2].RuleId, findings[2].Severity));
    }

    [Fact]
    public void TopLevelDatasetWithoutPushTaskIsYellow()
    {
        var inventory = new LabInventory(Now, [],
            [Sync("ssd_pool_one/files", Now.AddHours(-3))],
            [Dataset("ssd_pool_one/files"), Dataset("ssd_pool_one/photos")]);

        var finding = Assert.Single(Check.Evaluate(inventory));
        Assert.Equal(("cloudsync/not-off-box", Severity.Yellow, "ssd_pool_one/photos"),
            (finding.RuleId, finding.Severity, finding.Service));
    }

    [Fact]
    public void ChildDatasetOfSyncedParentCountsAsCovered()
    {
        // Sync tasks are exclude-based: a new folder/dataset under a synced parent is
        // picked up automatically (docs/backup-topology.md).
        var inventory = new LabInventory(Now, [],
            [Sync("ssd_pool_one/files", Now.AddHours(-3))],
            [Dataset("ssd_pool_one/files")]);

        Assert.Empty(Check.Evaluate(inventory));
    }
}
