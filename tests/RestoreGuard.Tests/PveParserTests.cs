using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Pve;

namespace RestoreGuard.Tests;

public class PveParserTests
{
    [Fact]
    public void ParsesLab99Guests()
    {
        var guests = PveResourcesParser.Parse(Fixtures.Read("pve-resources-lab99.json"));

        Assert.Equal(14, guests.Count);
        Assert.All(guests, g => Assert.Equal("pve", g.Node));

        var ubi = Assert.Single(guests, g => g.Name == "ubi");
        Assert.Equal((100, ServiceKind.Lxc, "running"), (ubi.Vmid, ubi.Kind, ubi.Status));

        var truenas = Assert.Single(guests, g => g.Vmid == 400);
        Assert.Equal("TrueNas.Scale.One", truenas.Name);
        Assert.Equal(ServiceKind.Vm, truenas.Kind);
        Assert.Equal("running", truenas.Status);
    }

    [Fact]
    public void ParsesLab142GuestsIncludingNewImmich()
    {
        var guests = PveResourcesParser.Parse(Fixtures.Read("pve-resources-lab142.json"));

        // immich (210) appeared after the docs were written — exactly the kind of
        // new-guest-never-added-to-backups drift the check must catch.
        var immich = Assert.Single(guests, g => g.Name == "immich");
        Assert.Equal((210, ServiceKind.Lxc, "running"), (immich.Vmid, immich.Kind, immich.Status));
    }

    [Fact]
    public void ParsesPbsContent()
    {
        var snapshots = PbsContentParser.Parse(Fixtures.Read("pbs-content.json"));

        Assert.Equal(32, snapshots.Count);
        Assert.All(snapshots, s => Assert.Equal("lxc", s.Subtype));
        Assert.Equal(8, snapshots.Count(s => s.Vmid == 100));

        var latestUbi = snapshots.Where(s => s.Vmid == 100).MaxBy(s => s.Ctime)!;
        Assert.Equal("ubi", latestUbi.Notes);
        Assert.Equal(new DateOnly(2026, 7, 4), DateOnly.FromDateTime(latestUbi.Ctime.UtcDateTime));
    }

    [Fact]
    public void ParsesStorageAndSharedFlag()
    {
        var storages = PveStorageParser.Parse(Fixtures.Read("pve-storage-lab142.json"), "host1");

        var pbs = Assert.Single(storages, s => s.Target.Name == "pbs-nvme");
        Assert.True(pbs.Shared);
        Assert.Equal("available", pbs.Target.Health);
        Assert.True(pbs.Target.CapacityBytes > 0);
        Assert.True(pbs.Target.FreeBytes > 0);

        var local = Assert.Single(storages, s => s.Target.Name == "local");
        Assert.False(local.Shared);
    }

    [Fact]
    public void MergeStoragesDedupsSharedOnly()
    {
        var s99 = PveStorageParser.Parse(Fixtures.Read("pve-storage-lab99.json"), "pve");
        var s142 = PveStorageParser.Parse(Fixtures.Read("pve-storage-lab142.json"), "host1");

        var merged = PveProvider.MergeStorages([.. s99, .. s142]);

        // The shared PBS datastore appears once; per-node "local" stays per node.
        Assert.Single(merged, s => s.Name == "pbs-nvme");
        Assert.Equal(2, merged.Count(s => s.Name == "local"));
    }
}
