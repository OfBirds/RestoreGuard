using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Tests;

public class ThreeTwoOneCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    // The lab shape: two nodes, a shared PBS datastore, and "local-dumps" existing
    // on BOTH nodes under the same name — the case that makes StoredOn necessary.
    private static readonly IReadOnlyList<StorageLocality> Storages =
    [
        new("pbs-main", "pve", Shared: true),
        new("pbs-main", "host1", Shared: true),
        new("local-dumps", "pve", Shared: false),
        new("local-dumps", "host1", Shared: false),
    ];

    private static Service Guest(string name, string node) =>
        new(name, node, ServiceKind.Lxc, "running", DeclaredMounts: null, LiveMounts: [], Image: null);

    private static BackupArtifact Image(string guest, string volid, string? storedOn, BackupTier tier = BackupTier.Vzdump) =>
        new(tier, guest, volid, Now.AddHours(-2), 1_000_000, "vzdump", HasOffsiteCopy: false, StoredOn: storedOn);

    private static List<Finding> Evaluate(IReadOnlyList<Service> services, IReadOnlyList<BackupArtifact> backups) =>
        new ThreeTwoOneCheck(Storages).Evaluate(new LabInventory(Now, services, backups, [])).ToList();

    [Fact]
    public void AllImageBackupsOnOwnNodeLocalStorage_IsYellow()
    {
        var findings = Evaluate(
            [Guest("ubi", "pve")],
            [Image("ubi", "local-dumps:backup/vzdump-lxc-100-2026_07_06.tar.zst", storedOn: "pve"),
             Image("ubi", "local-dumps:backup/vzdump-lxc-100-2026_07_05.tar.zst", storedOn: "pve")]);

        var f = Assert.Single(findings);
        Assert.Equal(("three-two-one/image-local-only", Severity.Yellow), (f.RuleId, f.Severity));
        Assert.Contains("local-dumps", f.Evidence);
        Assert.Contains("'pve'", f.Evidence);
    }

    [Fact]
    public void AnyCopyOnSharedStorage_ClearsTheFinding()
    {
        // The PBS artifact carries StoredOn=pve too (its content was listed from
        // there) — Shared must win over the stamp: the datastore isn't that box.
        var findings = Evaluate(
            [Guest("ubi", "pve")],
            [Image("ubi", "local-dumps:backup/vzdump-lxc-100-2026_07_06.tar.zst", storedOn: "pve"),
             Image("ubi", "pbs-main:backup/ct/100/2026-07-06T02:00:00Z", storedOn: "pve", BackupTier.PbsImage)]);

        Assert.Empty(findings);
    }

    [Fact]
    public void SameStorageNameOnAnotherNode_IsOffBox_NoFinding()
    {
        // The guest lives on pve; its dumps land on host1's "local-dumps" (same
        // name, different disk — e.g. after a migration). Single-copy, yes — but
        // NOT same-box, so this rule stays quiet.
        var findings = Evaluate(
            [Guest("ubi", "pve")],
            [Image("ubi", "local-dumps:backup/vzdump-lxc-100-2026_07_06.tar.zst", storedOn: "host1")]);

        Assert.Empty(findings);
    }

    [Fact]
    public void UnknownStorageUnknownNodeOrNoImages_StaysConservative()
    {
        var findings = Evaluate(
            [Guest("mystery", "pve"), Guest("unstamped", "pve"), Guest("uncovered", "pve")],
            [Image("mystery", "some-unseen-storage:backup/vzdump-lxc-7-x.tar.zst", storedOn: "pve"),
             Image("unstamped", "local-dumps:backup/vzdump-lxc-8-x.tar.zst", storedOn: null)]);

        // Unknown storage / no StoredOn stamp: can't prove locality — no finding.
        // No artifacts at all: that's image-backup/uncovered's job, not this rule's.
        Assert.Empty(findings);
    }

    [Fact]
    public void ContainersAreIgnored_OnlyGuestsHaveImageBackups()
    {
        var docker = new Service("app-prod", "lab98", ServiceKind.Container, "running", null, [], "postgres:16");

        Assert.Empty(Evaluate([docker], [Image("app-prod", "local-dumps:backup/whatever.tar.zst", storedOn: "lab98")]));
    }
}
