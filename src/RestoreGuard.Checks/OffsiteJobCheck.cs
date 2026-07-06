using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

public sealed record OffsiteJobExpectation(string Name, string Host, TimeSpan MaxSyncAge);

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
            var last = byName[expected.Name].OrderByDescending(b => b.Timestamp).FirstOrDefault();
            if (last is null)
            {
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
