using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

public sealed record OffsiteJobExpectation(string Name, string Host, TimeSpan MaxSyncAge, TimeSpan MaxRunAge = default, string? LogError = null)
{
    public TimeSpan EffectiveMaxRunAge => MaxRunAge == default ? TimeSpan.FromHours(6) : MaxRunAge;
}

/// <summary>
/// Generic off-site sync jobs (rclone wrapper scripts): each configured job must
/// have run at least once, its last run must have succeeded, and it must be
/// recent. Unlike the legacy pbs-offsite check this is expectation-driven, so a
/// job whose log has NO runs at all surfaces as its own finding instead of
/// silently checking nothing. Remote capacity rides on StorageCapacityCheck via
/// the rclone-about StorageTarget.
/// </summary>
public sealed class OffsiteJobCheck(IReadOnlyList<OffsiteJobExpectation> expectations) : ICheck
{
    public string RuleId => "offsite";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        var byName = inventory.Backups
            .Where(b => b.Tier == BackupTier.CloudSync && b.Method == "rclone-offsite")
            .ToLookup(b => b.TargetService, StringComparer.Ordinal);

        foreach (var expected in expectations)
        {
            if (expected.LogError is not null)
            {
                yield return new Finding(
                    "offsite/log-unreadable", Severity.Red, expected.Name, expected.Host,
                    $"Cannot read the sync log for '{expected.Name}': {expected.LogError}",
                    "Restore access to the configured log, then rerun the audit to verify the off-site copy.");
                continue;
            }

            var runs = byName[expected.Name].ToList();
            var active = runs.Where(b => b.Status == "running").OrderByDescending(b => b.Timestamp).FirstOrDefault();
            if (active is not null && inventory.CapturedAt - active.Timestamp > expected.EffectiveMaxRunAge)
            {
                yield return new Finding(
                    "offsite/hung", Severity.Red, expected.Name, expected.Host,
                    $"Off-site sync ({active.Location}) started at {active.Timestamp:u} and has no completion marker after {expected.EffectiveMaxRunAge.TotalHours:F0}h.",
                    "Check the process and log; a running marker is only healthy within the configured execution window.");
            }

            var last = runs.Where(b => b.Status != "running").OrderByDescending(b => b.Timestamp).FirstOrDefault();
            if (last is null)
            {
                if (active is not null)
                {
                    if (inventory.CapturedAt - active.Timestamp <= expected.EffectiveMaxRunAge)
                    {
                        yield return new Finding(
                            "offsite/in-progress", Severity.Yellow, expected.Name, expected.Host,
                            $"Off-site sync ({active.Location}) started at {active.Timestamp:u} and has not completed yet.",
                            "Wait for the completion marker; a run that exceeds the execution window becomes RED.");
                    }
                    continue;
                }
                yield return new Finding(
                    "offsite/never-ran", Severity.Red, expected.Name, expected.Host,
                    $"The sync log has no runs at all for '{expected.Name}'.",
                    "The job never ran (or its script doesn't log the '=== <ts> ... sync start ===' / "
                    + "'=== sync finished rc=N ===' markers) — run it once by hand and check the log format.");
                continue;
            }

            if (last.Status == "failed")
            {
                yield return new Finding(
                    "offsite/failed", Severity.Red, expected.Name, expected.Host,
                    $"Last off-site sync ({last.Location}) at {last.Timestamp:u} did not succeed.",
                    "Check the sync log — until it succeeds again, the off-site copy is behind and a single-site failure loses the gap.");
                continue;
            }

            var age = inventory.CapturedAt - last.Timestamp;
            if (age > expected.MaxSyncAge)
            {
                yield return new Finding(
                    "offsite/stale", Severity.Red, expected.Name, expected.Host,
                    $"Last successful off-site sync ({last.Location}) was {age.TotalHours:F0}h ago (limit {expected.MaxSyncAge.TotalHours:F0}h).",
                    "The scheduled sync stopped running — check its cron/timer entry and log.");
            }
        }
    }
}
