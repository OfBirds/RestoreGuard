using System.Globalization;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers.Zfs;

/// <summary>One ZFS dataset to watch: sanoid-style snapshot freshness on the
/// source, and optionally a syncoid/zfs-send replica whose newest snapshot must
/// also be fresh. Target fields come as a pair or not at all.</summary>
public sealed record ZfsReplicationConfig(
    string Name,
    string SourceAlias,
    string SourceDataset,
    string? TargetAlias = null,
    string? TargetDataset = null,
    double MaxSnapshotAgeHours = 26,
    double MaxReplicaAgeHours = 26);

/// <summary>
/// Read-only ZFS discovery over SSH: `zfs list -H -p` gives tab-separated,
/// machine-parseable output (creation as unix epoch) on every ZFS platform.
/// `-d 1` keeps it to the dataset's own snapshots — no recursion surprises.
/// </summary>
public sealed class ZfsProvider(ISshProvider ssh)
{
    public async Task<ZfsReplicationState> GetAsync(ZfsReplicationConfig config, CancellationToken ct = default)
    {
        var (sourceName, sourceTime) = await NewestSnapshotAsync(config.SourceAlias, config.SourceDataset, ct);

        (string?, DateTimeOffset?) target = (null, null);
        if (config.TargetAlias is { Length: > 0 } targetAlias && config.TargetDataset is { Length: > 0 } targetDataset)
            target = await NewestSnapshotAsync(targetAlias, targetDataset, ct);

        return new ZfsReplicationState(
            config.Name, config.SourceAlias, config.SourceDataset,
            config.TargetAlias, config.TargetDataset,
            sourceTime, sourceName, target.Item2, target.Item1);
    }

    public static string ListCommand(string dataset) =>
        $"zfs list -H -p -t snapshot -o name,creation -s creation -d 1 '{dataset}'";

    private async Task<(string? Name, DateTimeOffset? Time)> NewestSnapshotAsync(string alias, string dataset, CancellationToken ct)
    {
        var result = await ssh.RunAsync(alias, ListCommand(dataset), ct);
        if (result.ExitCode != 0)
            throw new ProviderException($"'zfs list' for '{dataset}' on {alias} failed: {result.StdErr.Trim()}");
        return ParseNewestSnapshot(result.StdOut);
    }

    /// <summary>`-s creation` sorts oldest→newest, so the newest is the last line.
    /// An empty listing (dataset exists, zero snapshots) parses to (null, null).</summary>
    public static (string? Name, DateTimeOffset? Time) ParseNewestSnapshot(string output)
    {
        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (last is null)
            return (null, null);

        var parts = last.Split('\t');
        if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            throw new ProviderException($"Unexpected `zfs list -H -p` line: '{last}'.");
        return (parts[0], DateTimeOffset.FromUnixTimeSeconds(epoch));
    }
}
