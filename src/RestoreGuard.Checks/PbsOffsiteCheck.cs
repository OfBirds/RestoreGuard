using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

public sealed record PbsOffsiteOptions(string Host, string TargetName, TimeSpan MaxSyncAge, TimeSpan MaxRunAge = default, string? LogError = null)
{
    public TimeSpan EffectiveMaxRunAge => MaxRunAge == default ? TimeSpan.FromHours(6) : MaxRunAge;
}

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
        if (options.LogError is not null)
        {
            yield return new Finding(
                "pbs/offsite-log-unreadable", Severity.Red, options.TargetName, options.Host,
                $"Cannot read the PBS off-site sync log: {options.LogError}",
                "Restore access to the configured log, then rerun the audit to verify the off-site copy.");
            yield break;
        }

        var syncs = inventory.Backups
            .Where(b => b.Tier == BackupTier.CloudSync && b.Method == "rclone-pbs-offsite")
            .ToList();

        var active = syncs.Where(s => s.Status == "running").OrderByDescending(s => s.Timestamp).FirstOrDefault();
        if (active is not null && inventory.CapturedAt - active.Timestamp > options.EffectiveMaxRunAge)
        {
            yield return new Finding(
                "pbs/offsite-hung", Severity.Red, active.TargetService, options.Host,
                $"Off-site sync ({active.Location}) started at {active.Timestamp:u} and has no completion marker after {options.EffectiveMaxRunAge.TotalHours:F0}h.",
                "Check the process and log; a running marker is only healthy within the configured execution window.");
        }

        var sync = syncs.Where(s => s.Status != "running").OrderByDescending(s => s.Timestamp).FirstOrDefault();
        if (sync is null)
        {
            if (active is not null && inventory.CapturedAt - active.Timestamp <= options.EffectiveMaxRunAge)
            {
                yield return new Finding(
                    "pbs/offsite-in-progress", Severity.Yellow, active.TargetService, options.Host,
                    $"Off-site sync ({active.Location}) started at {active.Timestamp:u} and has not completed yet.",
                    "Wait for the completion marker; a run that exceeds the execution window becomes RED.");
            }
            else if (active is null)
            {
                yield return new Finding(
                    "pbs/offsite-never-ran", Severity.Red, options.TargetName, options.Host,
                    "The PBS off-site sync log has no completed run markers.",
                    "Run the sync once and confirm it logs the documented start and completion markers.");
            }
            yield break;
        }

        if (sync.Status == "failed")
        {
            yield return new Finding(
                "pbs/offsite-failed", Severity.Red, sync.TargetService, options.Host,
                $"Last off-site sync ({sync.Location}) at {sync.Timestamp:u} did not succeed.",
                "Check the sync log — until it succeeds again, the off-site copy is behind and a single-site failure loses the gap.");
            yield break;
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
