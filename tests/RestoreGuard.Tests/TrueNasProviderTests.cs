using RestoreGuard.Core.Model;
using RestoreGuard.Providers;
using RestoreGuard.Providers.TrueNas;

namespace RestoreGuard.Tests;

/// <summary>Serves captured midclt fixtures keyed by the midclt method in the command.</summary>
file sealed class FakeMidcltSsh : ISshProvider
{
    public Task<SshResult> RunAsync(string hostAlias, string command, CancellationToken ct = default)
    {
        var fixture = command switch
        {
            _ when command.Contains("pool.query") => "truenas-pools.json",
            _ when command.Contains("pool.dataset.query") => "truenas-datasets.json",
            _ when command.Contains("zfs.snapshot.query") => "truenas-snapshots.json",
            _ when command.Contains("cloudsync.query") => "truenas-cloudsync.json",
            _ => throw new InvalidOperationException($"Unexpected command: {command}"),
        };
        return Task.FromResult(new SshResult(0, Fixtures.Read(fixture), ""));
    }
}

public class TrueNasProviderTests
{
    private static readonly TrueNasConfig Config =
        new("truenas", ["ssd_pool_one/management/system"]);

    private static Task<TrueNasProvider.TrueNasInventory> GetAsync() =>
        new TrueNasProvider(new FakeMidcltSsh()).GetAsync(Config);

    [Fact]
    public async Task PoolBecomesStorageTargetWithRootDatasetCapacity()
    {
        var inventory = await GetAsync();

        var pool = Assert.Single(inventory.Storage, s => s.Name == "ssd_pool_one");
        Assert.Equal("ONLINE", pool.Health);
        Assert.NotNull(pool.LastScrubOrGc);
        // Capacity = root dataset used+available (2.46T + 4.68T), NOT `df` at pool root.
        Assert.Equal(2705499510560 + 5145351886096, pool.CapacityBytes);
        Assert.Equal(5145351886096, pool.FreeBytes);
    }

    [Fact]
    public async Task ExcludedDatasetNeverEntersTheModel()
    {
        var inventory = await GetAsync();

        // The hard off-limits rule: management/system (and children) must not appear
        // anywhere — not as storage, not as a snapshot artifact.
        Assert.DoesNotContain(inventory.Storage,
            s => s.Name.StartsWith("ssd_pool_one/management/system", StringComparison.Ordinal));
        Assert.DoesNotContain(inventory.Artifacts,
            a => a.TargetService.StartsWith("ssd_pool_one/management/system", StringComparison.Ordinal));
        // …while the parent management dataset itself is still visible.
        Assert.Contains(inventory.Storage, s => s.Name == "ssd_pool_one/management");
    }

    [Fact]
    public async Task LatestAutoSnapshotPerDatasetBecomesArtifact()
    {
        var inventory = await GetAsync();

        var snaps = inventory.Artifacts.Where(a => a.Tier == BackupTier.ZfsSnapshot).ToList();
        Assert.NotEmpty(snaps);
        Assert.All(snaps, a => Assert.Equal("zfs-auto-snapshot", a.Method));

        var catalogs = Assert.Single(snaps, a => a.TargetService == "ssd_pool_one/ix-applications/catalogs");
        Assert.Equal(new DateOnly(2026, 7, 4), DateOnly.FromDateTime(catalogs.Timestamp.UtcDateTime));
        Assert.EndsWith("@auto-2026-07-04_00-00", catalogs.Location);
    }

    [Fact]
    public async Task PushTasksBecomeCloudSyncArtifacts_PullIsNotABackup()
    {
        var inventory = await GetAsync();

        var syncs = inventory.Artifacts.Where(a => a.Tier == BackupTier.CloudSync).ToList();
        // Live 2026-07-04: 5 tasks, 4 PUSH + 1 PULL (the PULL is a download, not a backup).
        Assert.Equal(4, syncs.Count);
        Assert.All(syncs, a => Assert.Equal("ok", a.Status));
        Assert.All(syncs, a => Assert.True(a.HasOffsiteCopy));
        Assert.Equal(
            ["ssd_pool_one/backups", "ssd_pool_one/files", "ssd_pool_one/management", "ssd_pool_one/misc"],
            syncs.Select(a => a.TargetService).Order(StringComparer.Ordinal).ToList());
    }
}
