using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

/// <summary>
/// 3-2-1 hygiene from data the audit already has: a guest whose EVERY image backup
/// lands on non-shared storage of its own node has a backup that dies with the box —
/// one disk/host failure takes the guest and all its copies together. YELLOW, not
/// RED: the backups are real and restore drills work, they just share fate with the
/// data. Storage localities are injected from PVE discovery; an artifact on storage
/// this run didn't see stays conservative (no finding).
/// </summary>
public sealed class ThreeTwoOneCheck(IReadOnlyList<StorageLocality> storages) : ICheck
{
    public string RuleId => "three-two-one";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        // Any Shared entry for a name means the storage is not tied to one box.
        // Names alone can't place non-shared storages ("local" exists on every
        // node) — that's what the artifact's StoredOn stamp is for.
        var sharedNames = storages.Where(s => s.Shared).Select(s => s.Name)
            .ToHashSet(StringComparer.Ordinal);
        var knownNames = storages.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        var imagesByGuest = inventory.Backups
            .Where(b => b.Tier is BackupTier.PbsImage or BackupTier.Vzdump)
            .ToLookup(b => b.TargetService, StringComparer.Ordinal);

        foreach (var guest in inventory.Services.Where(s => s.Kind is ServiceKind.Vm or ServiceKind.Lxc))
        {
            var images = imagesByGuest[guest.Name].ToList();
            if (images.Count == 0)
                continue; // no image backup at all is ImageBackupCheck's finding, not ours

            var allLocalToOwnNode = images.All(i =>
            {
                // Volids are "storage:backup/..." — the prefix names the storage.
                var name = i.Location.Split(':', 2)[0];
                return knownNames.Contains(name)
                    && !sharedNames.Contains(name)
                    && i.StoredOn == guest.Host;
            });
            if (!allLocalToOwnNode)
                continue;

            var storageNames = images
                .Select(i => i.Location.Split(':', 2)[0])
                .Distinct(StringComparer.Ordinal)
                .ToList();

            yield return new Finding(
                "three-two-one/image-local-only", Severity.Yellow, guest.Name, guest.Host,
                $"All {images.Count} image backup(s) of '{guest.Name}' live on local storage "
                + $"({string.Join(", ", storageNames)}) of its own node '{guest.Host}' — one disk or host "
                + "failure takes the guest and every copy of it together.",
                "Give this guest a copy that survives the node: back it up to a PBS datastore or shared "
                + "storage too, or sync the dump directory off the box.");
        }
    }
}
