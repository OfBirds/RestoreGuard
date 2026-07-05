using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Pve;

namespace RestoreGuard.Tests;

public class ImageBackupCheckTests
{
    // Late evening UTC on capture day: the vzdump run from 23:14 was minutes earlier.
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 23, 30, 0, TimeSpan.Zero);
    private static readonly ImageBackupCheck Check = new(new ImageBackupOptions(TimeSpan.FromHours(26)));

    private static LabInventory RealInventory()
    {
        List<PveGuest> guests =
        [
            .. PveResourcesParser.Parse(Fixtures.Read("pve-resources-lab99.json")),
            .. PveResourcesParser.Parse(Fixtures.Read("pve-resources-lab142.json")),
        ];
        List<PbsSnapshot> snapshots =
        [
            .. PbsContentParser.Parse(Fixtures.Read("pbs-content.json")),
            .. PbsContentParser.Parse(Fixtures.Read("vzdump-content.json"))
                .Select(s => s with { Tier = BackupTier.Vzdump }),
        ];
        var artifacts = PveArtifactAssembler.Join(guests, snapshots);
        return new LabInventory(Now, guests.Select(g => g.ToService()).ToList(), artifacts, []);
    }

    [Fact]
    public void GoldenFile_VzdumpArchiveCoversHaos()
    {
        var inventory = RealInventory();

        // The 2026-07-04 vzdump run: haos is now covered by a Tier-2 archive.
        var haosBackup = Assert.Single(inventory.Backups, b => b.TargetService == "haos");
        Assert.Equal(BackupTier.Vzdump, haosBackup.Tier);
        Assert.Equal("vma.zst", haosBackup.Method);
        Assert.Equal(3220201298, haosBackup.SizeBytes);

        var findings = Check.Evaluate(inventory).ToList();

        // haos dropped out of the uncovered list; the other gaps remain.
        Assert.All(findings, f => Assert.Equal("image-backup/uncovered", f.RuleId));
        Assert.Equal(
            ["TrueNas.Scale.One", "immich", "pbs", "programming"],
            findings.Select(f => f.Service).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void StaleSnapshotIsRed()
    {
        var guest = new Service("ubi", "pve", ServiceKind.Lxc, "running", null, [], null);
        var old = new BackupArtifact(BackupTier.PbsImage, "ubi", "pbs:backup/ct/100/x",
            Now.AddDays(-4), 1000, "pbs-ct", false);

        var finding = Assert.Single(Check.Evaluate(new LabInventory(Now, [guest], [old], [])));
        Assert.Equal("image-backup/stale", finding.RuleId);
        Assert.Equal(Severity.Red, finding.Severity);
    }

    [Fact]
    public void VzdumpArchiveCountsAsCoverageSameAsPbs()
    {
        var guest = new Service("haos", "pve", ServiceKind.Vm, "running", null, [], null);
        var archive = new BackupArtifact(BackupTier.Vzdump, "haos", "nas_backup:backup/vzdump-qemu-9000.vma.zst",
            Now.AddHours(-2), 1000, "vma.zst", false);

        Assert.Empty(Check.Evaluate(new LabInventory(Now, [guest], [archive], [])));
    }

    [Fact]
    public void OrphanArchiveForRemovedGuestIsYellow()
    {
        var inventory = new LabInventory(Now, [],
            [new BackupArtifact(BackupTier.PbsImage, "vmid 103 (source)", "pbs:backup/vm/103/x",
                Now.AddDays(-10), 1000, "pbs-vm", false)], []);

        var finding = Assert.Single(Check.Evaluate(inventory));
        Assert.Equal("image-backup/orphan", finding.RuleId);
        Assert.Equal(Severity.Yellow, finding.Severity);
    }

    [Fact]
    public void StoppedGuestWithArchivesIsNotOrphanOrUncovered()
    {
        var guest = new Service("debbie", "pve", ServiceKind.Vm, "stopped", null, [], null);
        var snap = new BackupArtifact(BackupTier.PbsImage, "debbie", "pbs:backup/vm/102/x",
            Now.AddDays(-30), 1000, "pbs-vm", false);

        Assert.Empty(Check.Evaluate(new LabInventory(Now, [guest], [snap], [])));
    }
}
