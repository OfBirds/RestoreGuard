using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

public sealed record PbsOffsiteOptions(string Host, TimeSpan MaxSyncAge);

/// <summary>
/// The PBS datastore's off-site copy (rclone → OneDrive): the last sync must exist,
/// have succeeded, and be recent. Remote capacity is covered by StorageCapacityCheck
/// via the rclone-about StorageTarget.
/// </summary>
public sealed class PbsOffsiteCheck(PbsOffsiteOptions options) : ICheck
{
    public string RuleId => "pbs-offsite";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        var syncs = inventory.Backups
            .Where(b => b.Tier == BackupTier.CloudSync && b.Method == "rclone-pbs-offsite")
            .ToList();

        foreach (var sync in syncs)
        {
            if (sync.Status == "failed")
            {
                yield return new Finding(
                    "pbs/offsite-failed", Severity.Red, sync.TargetService, options.Host,
                    $"Last off-site sync ({sync.Location}) at {sync.Timestamp:u} did not succeed.",
                    "Check the sync log — until it succeeds again, the off-site copy is behind and a single-site failure loses the gap.");
                continue;
            }

            var age = inventory.CapturedAt - sync.Timestamp;
            if (age > options.MaxSyncAge)
            {
                yield return new Finding(
                    "pbs/offsite-stale", Severity.Red, sync.TargetService, options.Host,
                    $"Last successful off-site sync ({sync.Location}) was {age.TotalHours:F0}h ago (limit {options.MaxSyncAge.TotalHours:F0}h).",
                    "The daily sync stopped running — check its cron entry and log.");
            }
        }
    }
}
