using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

public sealed record FileBackupExpectation(string Name, string Host, TimeSpan MaxAge);

/// <summary>
/// One check for every file-level backup source (restic repos, archive directories,
/// HA full backups): each configured source must have at least one artifact, the
/// latest must be fresh, and an archive of 0 bytes is the classic silent failure.
/// A configured source with NO artifacts is RED — that's how "the repo is empty" and
/// "HA has never made a full backup" surface.
/// </summary>
public sealed class FileBackupCheck(IReadOnlyList<FileBackupExpectation> expectations) : ICheck
{
    public string RuleId => "file-backup";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        var byName = inventory.Backups
            .Where(b => b.Tier == BackupTier.FileBackup)
            .ToLookup(b => b.TargetService, StringComparer.Ordinal);

        foreach (var expected in expectations)
        {
            var latest = byName[expected.Name].OrderByDescending(b => b.Timestamp).FirstOrDefault();
            if (latest is null)
            {
                yield return new Finding(
                    "file-backup/uncovered", Severity.Red, expected.Name, expected.Host,
                    $"Configured file backup '{expected.Name}' has no artifacts at all.",
                    "The job never produced a backup (empty repo / empty directory / no full HA backup) — run it once and check its schedule.");
                continue;
            }

            var age = inventory.CapturedAt - latest.Timestamp;
            if (age > expected.MaxAge)
            {
                yield return new Finding(
                    "file-backup/stale", Severity.Red, expected.Name, expected.Host,
                    $"Latest artifact ({latest.Location}) is {age.TotalHours:F0}h old (limit {expected.MaxAge.TotalHours:F0}h).",
                    "The schedule stopped producing backups — check the job's log/cron.");
            }

            if (latest.SizeBytes == 0)
            {
                yield return new Finding(
                    "file-backup/empty", Severity.Red, expected.Name, expected.Host,
                    $"Latest artifact ({latest.Location}) is 0 bytes.",
                    "The backup 'ran' but captured nothing — the silent-empty-backup failure. Investigate before trusting it.");
            }
        }
    }
}
