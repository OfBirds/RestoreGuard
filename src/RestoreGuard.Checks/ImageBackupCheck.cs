using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

public sealed record ImageBackupOptions(TimeSpan MaxSnapshotAge);

/// <summary>
/// Image-tier cross-check: every running VM/LXC guest should have a fresh image
/// backup — PBS snapshot or plain vzdump archive, either counts — and archives for
/// guests that no longer exist are surfaced as orphans. Intentional gaps (a guest
/// covered by another tier) belong in suppressions, so the decision stays auditable
/// instead of hardcoded.
/// </summary>
public sealed class ImageBackupCheck(ImageBackupOptions options) : ICheck
{
    public string RuleId => "image-backup";

    private static bool IsImageTier(BackupArtifact b) =>
        b.Tier is BackupTier.PbsImage or BackupTier.Vzdump;

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        var snapshots = inventory.Backups
            .Where(IsImageTier)
            .ToLookup(b => b.TargetService, StringComparer.Ordinal);

        var guests = inventory.Services
            .Where(s => s.Kind is ServiceKind.Vm or ServiceKind.Lxc)
            .ToList();

        foreach (var guest in guests.Where(g => g.State == "running"))
        {
            var latest = snapshots[guest.Name].OrderByDescending(b => b.Timestamp).FirstOrDefault();
            if (latest is null)
            {
                yield return new Finding(
                    "image-backup/uncovered", Severity.Red, guest.Name, guest.Host,
                    $"Running {guest.Kind} guest '{guest.Name}' has no image backup at all (neither PBS nor vzdump).",
                    "Add the guest to a backup job, or suppress with the tier that covers it instead.");
                continue;
            }

            var age = inventory.CapturedAt - latest.Timestamp;
            if (age > options.MaxSnapshotAge)
            {
                yield return new Finding(
                    "image-backup/stale", Severity.Red, guest.Name, guest.Host,
                    $"Latest image backup {latest.Location} is {age.TotalHours:F0}h old (limit {options.MaxSnapshotAge.TotalHours:F0}h).",
                    "The backup job stopped producing archives for this guest — check the vzdump job log / PBS task history.");
            }

            if (latest.SizeBytes == 0)
            {
                yield return new Finding(
                    "image-backup/empty", Severity.Red, guest.Name, guest.Host,
                    $"Latest image backup {latest.Location} reports 0 bytes.",
                    "The archive is empty — verify the guest's disks are actually included in the job.");
            }
        }

        // Orphans: archives whose target is no guest we know (any state). A stopped
        // guest with old archives is a cold fallback, not an orphan.
        var knownNames = guests.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var group in snapshots.Where(g => !knownNames.Contains(g.Key)))
        {
            var latest = group.OrderByDescending(b => b.Timestamp).First();
            yield return new Finding(
                "image-backup/orphan", Severity.Yellow, group.Key, "-",
                $"{group.Count()} image backup(s) target '{group.Key}', which matches no existing guest (latest: {latest.Location}).",
                "The guest was removed or renamed. Prune the archives when no longer needed, or investigate if removal was unintended.");
        }
    }
}
