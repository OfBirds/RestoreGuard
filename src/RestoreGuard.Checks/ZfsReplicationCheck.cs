using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

public sealed record ZfsReplicationExpectation(
    string Name,
    TimeSpan MaxSnapshotAge,
    TimeSpan MaxReplicaAge);

/// <summary>
/// Sanoid/syncoid-style ZFS setups fail in two quiet ways: the snapshot job dies
/// (source stops getting snapshots) or the replication dies (the replica keeps its
/// old snapshots and looks "fine" at a glance). Both are RED — either one means
/// the copy you'd restore from is drifting away from reality. States are injected
/// from discovery (the PbsMaintenanceCheck pattern); rules stay pure.
/// </summary>
public sealed class ZfsReplicationCheck(
    IReadOnlyList<ZfsReplicationState> states,
    IReadOnlyList<ZfsReplicationExpectation> expectations) : ICheck
{
    public string RuleId => "zfs-replication";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        var byName = expectations.ToDictionary(e => e.Name, StringComparer.Ordinal);

        foreach (var s in states)
        {
            if (!byName.TryGetValue(s.Name, out var expected))
                continue;

            if (s.NewestSourceSnapshot is not { } newestSource)
            {
                yield return new Finding(
                    "zfs-replication/no-snapshots", Severity.Red, s.Name, s.SourceHost,
                    $"Dataset '{s.SourceDataset}' has no snapshots at all.",
                    "The snapshot job (sanoid/zfs-auto-snapshot/cron) never ran here — check its service and config.");
            }
            else
            {
                var age = inventory.CapturedAt - newestSource;
                if (age > expected.MaxSnapshotAge)
                {
                    yield return new Finding(
                        "zfs-replication/snapshot-stale", Severity.Red, s.Name, s.SourceHost,
                        $"Newest snapshot of '{s.SourceDataset}' ({s.NewestSourceName}) is {age.TotalHours:F0}h old (limit {expected.MaxSnapshotAge.TotalHours:F0}h).",
                        "The snapshot job stopped — check sanoid/the cron job and its logs on the source host.");
                }
            }

            if (s.TargetDataset is not { Length: > 0 })
                continue; // snapshot-only entry: no replica to judge

            if (s.NewestTargetSnapshot is not { } newestTarget)
            {
                yield return new Finding(
                    "zfs-replication/replica-missing", Severity.Red, s.Name, s.TargetHost ?? "",
                    $"Replica dataset '{s.TargetDataset}' has no snapshots — nothing has ever been replicated into it.",
                    "Run the syncoid/zfs-send job once by hand and check why it never populated the target.");
            }
            else
            {
                var age = inventory.CapturedAt - newestTarget;
                if (age > expected.MaxReplicaAge)
                {
                    yield return new Finding(
                        "zfs-replication/replica-stale", Severity.Red, s.Name, s.TargetHost ?? "",
                        $"Newest replicated snapshot on '{s.TargetDataset}' ({s.NewestTargetName}) is {age.TotalHours:F0}h old (limit {expected.MaxReplicaAge.TotalHours:F0}h) — replication stopped while the source kept going.",
                        "Check the syncoid/zfs-send schedule and its last run's output; the replica is drifting away from reality.");
                }
            }
        }
    }
}
