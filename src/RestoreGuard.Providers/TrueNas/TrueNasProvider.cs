using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers.TrueNas;

public sealed record TrueNasConfig(string Alias, IReadOnlyList<string> ExcludeDatasets);

/// <summary>
/// Read-only TrueNAS discovery via `midclt call` over SSH (zpool/zfs aren't in the
/// admin PATH; midclt is the sanctioned surface). Queries use `select` so credential
/// blobs (cloud-sync OAuth tokens!) never leave the appliance. ExcludeDatasets is a
/// hard privacy boundary (the lab's management/system) — excluded datasets are dropped
/// before they enter the model at all.
/// </summary>
public sealed class TrueNasProvider(ISshProvider ssh)
{
    public sealed record TrueNasInventory(
        IReadOnlyList<StorageTarget> Storage,
        IReadOnlyList<BackupArtifact> Artifacts);

    public async Task<TrueNasInventory> GetAsync(TrueNasConfig config, CancellationToken ct = default)
    {
        var pools = TrueNasParsers.ParsePools(
            await RunAsync(config.Alias, "midclt call pool.query", ct));
        var datasets = TrueNasParsers.ParseDatasets(
            await RunAsync(config.Alias, """midclt call pool.dataset.query '[]' '{"select":["id","used","available"]}'""", ct));
        var snapshots = TrueNasParsers.ParseLatestAutoSnapshots(
            await RunAsync(config.Alias, """midclt call zfs.snapshot.query '[]' '{"select":["name"]}'""", ct));
        var syncTasks = TrueNasParsers.ParseCloudSyncTasks(
            await RunAsync(config.Alias, """midclt call cloudsync.query '[]' '{"select":["id","description","path","direction","enabled","schedule","attributes","job"]}'""", ct));

        var storage = new List<StorageTarget>();
        var artifacts = new List<BackupArtifact>();

        var byId = datasets.ToDictionary(d => d.Id, StringComparer.Ordinal);
        foreach (var pool in pools)
        {
            // Capacity comes from the pool's root dataset — `df` at the pool root lies
            // because child datasets mount separately (docs/live-verification).
            var root = byId.GetValueOrDefault(pool.Name);
            storage.Add(new StorageTarget(
                Name: pool.Name,
                Host: config.Alias,
                CapacityBytes: root is null ? 0 : root.UsedBytes + root.AvailableBytes,
                FreeBytes: root?.AvailableBytes ?? 0,
                Health: pool.ScrubErrors > 0 ? $"{pool.Status} (scrub errors: {pool.ScrubErrors})" : pool.Status,
                LastScrubOrGc: pool.LastScrub));
        }

        foreach (var ds in datasets.Where(d => d.Id.Contains('/') && !IsExcluded(d.Id, config)))
        {
            storage.Add(new StorageTarget(
                Name: ds.Id,
                Host: config.Alias,
                CapacityBytes: ds.UsedBytes + ds.AvailableBytes,
                FreeBytes: ds.AvailableBytes,
                Health: "available",
                LastScrubOrGc: null));
        }

        foreach (var (dataset, (latest, name)) in snapshots)
        {
            if (IsExcluded(dataset, config))
                continue;
            artifacts.Add(new BackupArtifact(
                Tier: BackupTier.ZfsSnapshot,
                TargetService: dataset,
                Location: name,
                Timestamp: latest,
                SizeBytes: 0,
                Method: "zfs-auto-snapshot",
                HasOffsiteCopy: false,
                Status: "ok"));
        }

        foreach (var task in syncTasks.Where(t => t.Direction == "PUSH"))
        {
            artifacts.Add(new BackupArtifact(
                Tier: BackupTier.CloudSync,
                TargetService: task.Path.StartsWith("/mnt/", StringComparison.Ordinal) ? task.Path[5..] : task.Path,
                Location: $"cloudsync task {task.Id}: {task.Description} -> {task.DestinationFolder}",
                Timestamp: task.TimeFinished ?? DateTimeOffset.MinValue,
                SizeBytes: 0,
                Method: "rclone-push",
                HasOffsiteCopy: true,
                Status: !task.Enabled ? "disabled" : task.JobState == "SUCCESS" ? "ok" : "failed"));
        }

        return new TrueNasInventory(storage, artifacts);
    }

    private static bool IsExcluded(string dataset, TrueNasConfig config) =>
        config.ExcludeDatasets.Any(ex =>
            dataset.Equals(ex, StringComparison.Ordinal)
            || dataset.StartsWith(ex + "/", StringComparison.Ordinal));

    private async Task<string> RunAsync(string alias, string command, CancellationToken ct)
    {
        var result = await ssh.RunAsync(alias, command, ct);
        if (result.ExitCode != 0)
            throw new ProviderException($"'{command}' on {alias} failed: {result.StdErr.Trim()}");
        return result.StdOut;
    }
}
