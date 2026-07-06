using RestoreGuard.Core.Model;

namespace RestoreGuard.Providers.Pve;

/// <summary>
/// Joins PBS snapshots to guests. The lab runs two independent PVE nodes sharing one
/// PBS datastore, so vmid alone is AMBIGUOUS (vmid 100 = ubi LXC on pve, haos VM on
/// host1). Match on (vmid, guest kind), then disambiguate remaining ties via the
/// snapshot's notes (vzdump writes the guest name there). Unmatched snapshots keep a
/// synthetic "vmid N" target so the orphan check can surface them.
/// </summary>
public static class PveArtifactAssembler
{
    public static IReadOnlyList<BackupArtifact> Join(
        IReadOnlyList<PveGuest> guests, IReadOnlyList<PbsSnapshot> snapshots)
    {
        return snapshots.Select(snap =>
        {
            var candidates = guests
                .Where(g => g.Vmid == snap.Vmid && KindMatches(g.Kind, snap.Subtype))
                .ToList();

            if (candidates.Count > 1 && snap.Notes is not null)
            {
                var byNotes = candidates
                    .Where(g => snap.Notes == g.Name || snap.Notes.StartsWith(g.Name + " ", StringComparison.Ordinal))
                    .ToList();
                if (byNotes.Count == 1)
                    candidates = byNotes;
            }

            var target = candidates.Count == 1
                ? candidates[0].Name
                : $"vmid {snap.Vmid} ({snap.Notes ?? snap.Subtype})";

            return new BackupArtifact(
                Tier: snap.Tier,
                TargetService: target,
                Location: snap.Volid,
                Timestamp: snap.Ctime,
                SizeBytes: snap.Size,
                Method: snap.Format.Length > 0 ? snap.Format : (snap.Subtype == "lxc" ? "pbs-ct" : "pbs-vm"),
                HasOffsiteCopy: false,
                StoredOn: snap.Node.Length > 0 ? snap.Node : null);
        }).ToList();
    }

    private static bool KindMatches(ServiceKind kind, string subtype) =>
        (kind, subtype) is (ServiceKind.Lxc, "lxc") or (ServiceKind.Vm, "qemu");
}
