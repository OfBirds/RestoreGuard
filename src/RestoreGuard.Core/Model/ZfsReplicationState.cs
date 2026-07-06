namespace RestoreGuard.Core.Model;

/// <summary>
/// What one configured ZFS snapshot/replication pair looked like at discovery:
/// the newest snapshot on the source dataset (is sanoid/the snapshot job alive?)
/// and, when a target is configured, the newest snapshot on the replica (is
/// syncoid/zfs send still shipping?). Null timestamps mean the dataset exists
/// but has no snapshots at all — a distinct, worse condition than stale.
/// </summary>
public sealed record ZfsReplicationState(
    string Name,
    string SourceHost,
    string SourceDataset,
    string? TargetHost,
    string? TargetDataset,
    DateTimeOffset? NewestSourceSnapshot,
    string? NewestSourceName,
    DateTimeOffset? NewestTargetSnapshot,
    string? NewestTargetName);
