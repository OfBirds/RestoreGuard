using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

/// <summary>
/// Runs every rule over the inventory and applies the suppression filter.
/// Suppressed findings are kept and reported separately — never silently dropped —
/// and suppressions themselves are checked for rot: an expired entry, or one whose
/// target no longer exists anywhere in the inventory, fails loud as a finding
/// (the design requirement from docs/known-exceptions.md).
/// </summary>
public sealed class CheckEngine(IReadOnlyList<ICheck> checks)
{
    public Report Run(LabInventory inventory, IReadOnlyList<Suppression> suppressions, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var active = new List<Finding>();
        var suppressed = new List<Finding>();

        foreach (var finding in checks.SelectMany(c => c.Evaluate(inventory)))
        {
            var bucket = suppressions.Any(s => s.Matches(finding, today)) ? suppressed : active;
            bucket.Add(finding);
        }

        // Suppression hygiene runs after filtering and is itself unsuppressible.
        active.AddRange(AuditSuppressions(inventory, suppressions, today));

        return new Report(now, active, suppressed, suppressions);
    }

    private static IEnumerable<Finding> AuditSuppressions(
        LabInventory inventory, IReadOnlyList<Suppression> suppressions, DateOnly today)
    {
        // Everything a finding's (host, service) can legitimately reference:
        // services, compose projects, storage targets, and backup artifact targets.
        var known = new HashSet<(string Host, string Service)>(
            inventory.Services.Select(s => (s.Host.ToLowerInvariant(), s.Name.ToLowerInvariant())));
        foreach (var s in inventory.Services.Where(s => s.ComposeProject is not null))
            known.Add((s.Host.ToLowerInvariant(), s.ComposeProject!.ToLowerInvariant()));
        foreach (var st in inventory.Storage)
            known.Add((st.Host.ToLowerInvariant(), st.Name.ToLowerInvariant()));

        foreach (var s in suppressions)
        {
            if (s.Expires is { } expires && today > expires)
            {
                yield return new Finding(
                    "suppression/expired", Severity.Yellow, s.Service, s.Host,
                    $"Suppression for [{s.RuleId}] expired {expires:yyyy-MM-dd} but is still configured (reason was: {s.Reason}).",
                    "Re-decide: renew with a new expiry, or delete the entry. Expired suppressions no longer hide anything.");
            }
            else if (!known.Contains((s.Host.ToLowerInvariant(), s.Service.ToLowerInvariant())))
            {
                yield return new Finding(
                    "suppression/unknown-target", Severity.Yellow, s.Service, s.Host,
                    $"Suppression for [{s.RuleId}] references '{s.Host}/{s.Service}', which matches nothing in the current inventory.",
                    "The target was removed or renamed — the dead-entry rot that hid the Zitadel->Keycloak drift. Delete or update the entry.");
            }
        }
    }
}
