using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.FileBackups;

namespace RestoreGuard.Tests;

public class FileBackupTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 23, 30, 0, TimeSpan.Zero);

    [Fact]
    public void GoldenFile_ResticSnapshotsParse()
    {
        var artifacts = FileBackupProvider.ParseResticSnapshots(
            Fixtures.Read("restic-snapshots.json"), "restic lab98 /opt configs");

        var snap = Assert.Single(artifacts);
        Assert.Equal(BackupTier.FileBackup, snap.Tier);
        Assert.Equal("restic", snap.Method);
        Assert.Contains("/opt/monitoring", snap.Location);
        Assert.Equal(new DateOnly(2026, 7, 4), DateOnly.FromDateTime(snap.Timestamp.UtcDateTime));
        Assert.Equal(-1, snap.SizeBytes); // size unknown in the listing — must not read as "empty"
    }

    [Fact]
    public void GoldenFile_BorgArchivesParse()
    {
        var artifacts = FileBackupProvider.ParseBorgArchives(
            Fixtures.Read("borg-list.json"), "borg lab55 /opt+/etc");

        var archive = Assert.Single(artifacts);
        Assert.Equal(BackupTier.FileBackup, archive.Tier);
        Assert.Equal("borg", archive.Method);
        Assert.Contains("lab55-2026-07-05_0837", archive.Location);
        Assert.Equal(new DateOnly(2026, 7, 5), DateOnly.FromDateTime(archive.Timestamp.UtcDateTime));
        Assert.Equal(-1, archive.SizeBytes); // size unknown in the listing — must not read as "empty"
    }

    [Fact]
    public void GoldenFile_KopiaSnapshotsParse_WithRealSizes()
    {
        var artifacts = FileBackupProvider.ParseKopiaSnapshots(
            Fixtures.Read("kopia-list.json"), "kopia lab118");

        // Live capture: /etc and /opt/volume-backup, one snapshot each.
        Assert.Equal(2, artifacts.Count);
        Assert.All(artifacts, a => Assert.Equal("kopia", a.Method));
        var etc = Assert.Single(artifacts, a => a.Location.Contains("/etc"));
        Assert.Equal(8732179, etc.SizeBytes); // kopia reports real sizes, unlike restic/borg listings
        Assert.Equal(new DateOnly(2026, 7, 5), DateOnly.FromDateTime(etc.Timestamp.UtcDateTime));
    }

    [Fact]
    public void GoldenFile_SnapperSnapshotsParse_SkippingCurrent()
    {
        var artifacts = FileBackupProvider.ParseSnapperSnapshots(
            Fixtures.Read("snapper-list.json"), "snapper lab55 rgdata");

        // Live capture: entry #0 ("current", empty date) skipped; 2 real snapshots.
        Assert.Equal(2, artifacts.Count);
        Assert.All(artifacts, a => Assert.Equal("snapper", a.Method));
        Assert.Contains(artifacts, a => a.Location == "snapper #1 (baseline)");
        Assert.All(artifacts, a =>
            Assert.Equal(new DateOnly(2026, 7, 5), DateOnly.FromDateTime(a.Timestamp.UtcDateTime)));
    }

    [Fact]
    public void GoldenFile_VolumeBackupTarballParses()
    {
        var artifacts = FileBackupProvider.ParseDirListing(
            Fixtures.Read("volume-backups-listing.tsv"), "technitium-config volume lab118", "lab118:/opt/volume-backups");

        var tar = Assert.Single(artifacts);
        Assert.Equal(2012272, tar.SizeBytes);
        Assert.Equal("archive", tar.Method);
        Assert.Contains("technitium-config-2026-07-04", tar.Location);
    }

    [Fact]
    public void GoldenFile_HaBackupsKeepOnlyFullSystemBackups()
    {
        var artifacts = FileBackupProvider.ParseHaBackups(
            Fixtures.Read("ha-backups.json"), "home-assistant full backup");

        // The live capture has 2 partial pre-update add-on backups (Matter, Z2M) that
        // must be excluded — they don't make the system restorable — plus the one
        // full baseline backup created 2026-07-04.
        var full = Assert.Single(artifacts);
        Assert.Equal("ha-full", full.Method);
        Assert.Contains("restoreguard-baseline-20260704", full.Location);
        Assert.True(full.SizeBytes > 0);
    }

    [Fact]
    public void ConfiguredSourceWithoutArtifactsIsRedUncovered()
    {
        var check = new FileBackupCheck([new FileBackupExpectation("restic somewhere", "lab98", TimeSpan.FromHours(26))]);

        var finding = Assert.Single(check.Evaluate(new LabInventory(Now, [], [], [])));
        Assert.Equal(("file-backup/uncovered", Severity.Red), (finding.RuleId, finding.Severity));
    }

    [Fact]
    public void StaleAndEmptyArtifactsAreRed_ResticUnknownSizeIsNot()
    {
        BackupArtifact Artifact(string name, DateTimeOffset ts, long size, string method) =>
            new(BackupTier.FileBackup, name, name, ts, size, method, false, "ok");

        var check = new FileBackupCheck(
        [
            new FileBackupExpectation("stale-src", "h", TimeSpan.FromHours(26)),
            new FileBackupExpectation("empty-src", "h", TimeSpan.FromHours(26)),
            new FileBackupExpectation("restic-src", "h", TimeSpan.FromHours(26)),
        ]);

        var findings = check.Evaluate(new LabInventory(Now, [],
        [
            Artifact("stale-src", Now.AddDays(-3), 100, "archive"),
            Artifact("empty-src", Now.AddHours(-2), 0, "archive"),
            Artifact("restic-src", Now.AddHours(-2), -1, "restic"),
        ], [])).ToList();

        Assert.Equal(2, findings.Count);
        Assert.Equal("file-backup/stale", Assert.Single(findings, f => f.Service == "stale-src").RuleId);
        Assert.Equal("file-backup/empty", Assert.Single(findings, f => f.Service == "empty-src").RuleId);
    }
}
