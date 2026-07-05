using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

public sealed record TrueNasBackupOptions(
    string Host,
    TimeSpan MaxSnapshotAge,
    TimeSpan MaxSyncAge);

/// <summary>
/// Tier-4 checks: daily ZFS auto-snapshots fresh per dataset, cloud-sync push tasks
/// enabled + succeeding + recent, and every top-level dataset either off-box via some
/// push task or explicitly suppressed (the docs' "new top-level dataset needs its own
/// sync task" pitfall).
/// </summary>
public sealed class TrueNasBackupCheck(TrueNasBackupOptions options) : ICheck
{
    public string RuleId => "truenas";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        foreach (var snap in inventory.Backups.Where(b => b.Tier == BackupTier.ZfsSnapshot))
        {
            var age = inventory.CapturedAt - snap.Timestamp;
            if (age > options.MaxSnapshotAge)
            {
                yield return new Finding(
                    "zfs/snapshot-stale", Severity.Red, snap.TargetService, options.Host,
                    $"Latest auto-snapshot {snap.Location} is {age.TotalHours:F0}h old (limit {options.MaxSnapshotAge.TotalHours:F0}h).",
                    "The periodic snapshot task stopped covering this dataset — check the task's schedule and last run.");
            }
        }

        var pushTasks = inventory.Backups.Where(b => b.Tier == BackupTier.CloudSync).ToList();
        foreach (var task in pushTasks)
        {
            switch (task.Status)
            {
                case "failed":
                    yield return new Finding(
                        "cloudsync/failed", Severity.Red, task.TargetService, options.Host,
                        $"{task.Location}: last run did not succeed.",
                        "Check the task's job log in the TrueNAS UI; the off-site copy is not current.");
                    break;
                case "disabled":
                    yield return new Finding(
                        "cloudsync/disabled", Severity.Yellow, task.TargetService, options.Host,
                        $"{task.Location}: task is disabled.",
                        "Re-enable, or suppress if this data is intentionally no longer synced off-box.");
                    break;
                default:
                    var age = inventory.CapturedAt - task.Timestamp;
                    if (age > options.MaxSyncAge)
                    {
                        yield return new Finding(
                            "cloudsync/stale", Severity.Red, task.TargetService, options.Host,
                            $"{task.Location}: last successful run was {age.TotalHours:F0}h ago (limit {options.MaxSyncAge.TotalHours:F0}h).",
                            "The scheduled sync stopped running — check the schedule and the cloud credential.");
                    }
                    break;
            }
        }

        // Off-box coverage: every top-level dataset should be under some push task's
        // source, or consciously suppressed ("not off-box by choice" list in the docs).
        var topLevel = inventory.Storage
            .Where(s => s.Host == options.Host && s.Name.Count(c => c == '/') == 1);
        foreach (var ds in topLevel)
        {
            var covered = pushTasks.Any(t =>
                t.TargetService == ds.Name
                || ds.Name.StartsWith(t.TargetService + "/", StringComparison.Ordinal)
                || t.TargetService.StartsWith(ds.Name + "/", StringComparison.Ordinal));
            if (!covered)
            {
                yield return new Finding(
                    "cloudsync/not-off-box", Severity.Yellow, ds.Name, options.Host,
                    $"Top-level dataset '{ds.Name}' is not the source of any cloud-sync push task — it exists only on this box (snapshots live on the same pool).",
                    "Add a push task for it, or suppress with the documented 'not off-box by choice' reason.");
            }
        }
    }
}
