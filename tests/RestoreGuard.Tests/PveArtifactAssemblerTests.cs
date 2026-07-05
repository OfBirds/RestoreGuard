using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Pve;

namespace RestoreGuard.Tests;

/// <summary>
/// The lab's two PVE nodes share one PBS datastore with overlapping vmids — these
/// tests pin the disambiguation rules that keep backups attributed to the right guest.
/// </summary>
public class PveArtifactAssemblerTests
{
    private static List<PveGuest> AllGuests() =>
    [
        .. PveResourcesParser.Parse(Fixtures.Read("pve-resources-lab99.json")),
        .. PveResourcesParser.Parse(Fixtures.Read("pve-resources-lab142.json")),
    ];

    [Fact]
    public void GoldenFile_CollidingVmidsResolveByKind()
    {
        var artifacts = PveArtifactAssembler.Join(
            AllGuests(), PbsContentParser.Parse(Fixtures.Read("pbs-content.json")));

        Assert.Equal(32, artifacts.Count);
        Assert.All(artifacts, a => Assert.Equal(BackupTier.PbsImage, a.Tier));

        // vmid 100: ubi is LXC on pve, haos is VM on host1 — ct snapshots belong to ubi.
        Assert.Equal(8, artifacts.Count(a => a.TargetService == "ubi"));
        // vmid 102: debbie is a stopped VM on pve, boombox is LXC on host1.
        Assert.Equal(8, artifacts.Count(a => a.TargetService == "boombox"));
        // vmid 104: programming is a VM on pve, plex is LXC on host1 — ct/104 is plex.
        Assert.Equal(8, artifacts.Count(a => a.TargetService == "plex"));
        Assert.DoesNotContain(artifacts, a => a.TargetService == "programming");
        Assert.Equal(8, artifacts.Count(a => a.TargetService == "pdm"));
    }

    [Fact]
    public void SameKindCollisionFallsBackToNotes()
    {
        List<PveGuest> guests =
        [
            new(500, "alpha", "pve", ServiceKind.Lxc, "running"),
            new(500, "bravo", "host1", ServiceKind.Lxc, "running"),
        ];
        List<PbsSnapshot> snaps =
        [
            new(500, "lxc", DateTimeOffset.UtcNow, 100, "bravo", "pbs:backup/ct/500/x"),
        ];

        var artifact = Assert.Single(PveArtifactAssembler.Join(guests, snaps));
        Assert.Equal("bravo", artifact.TargetService);
    }

    [Fact]
    public void UnresolvableSnapshotGetsSyntheticVmidTarget()
    {
        List<PveGuest> guests = [new(500, "alpha", "pve", ServiceKind.Vm, "running")];
        List<PbsSnapshot> snaps =
        [
            new(999, "lxc", DateTimeOffset.UtcNow, 100, "ghost", "pbs:backup/ct/999/x"),
        ];

        var artifact = Assert.Single(PveArtifactAssembler.Join(guests, snaps));
        Assert.Equal("vmid 999 (ghost)", artifact.TargetService);
    }
}
