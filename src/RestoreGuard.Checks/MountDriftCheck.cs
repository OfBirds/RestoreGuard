using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

/// <summary>
/// The headline check: does each compose-managed container actually mount what its
/// compose file declares? Catches the renamed-bind-mount-during-refactor failure
/// (backups keep "succeeding" against the old, now-empty path).
/// </summary>
public sealed class MountDriftCheck : ICheck
{
    public string RuleId => "mount-drift";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        // Stopped containers keep stale mount records; only running ones can drift meaningfully.
        var candidates = inventory.Services
            .Where(s => s.ComposeProject is not null && s.State == "running")
            .ToList();

        // One finding per unresolvable project, not per container — 6 containers of one
        // Portainer stack are one problem, not six.
        foreach (var project in candidates
                     .Where(s => s.DeclaredMounts is null)
                     .GroupBy(s => (s.Host, s.ComposeProject)))
        {
            yield return new Finding(
                "mount-drift/unresolved-declared", Severity.Yellow, project.Key.ComposeProject!, project.Key.Host,
                $"Compose project '{project.Key.ComposeProject}' ({string.Join(", ", project.Select(s => s.Name))}): declared config could not be resolved on the host (project dir missing or `docker compose config` failed — typical for Portainer-managed stacks). Mount drift cannot be checked.",
                "Make the compose project resolvable on the host, or suppress if this stack is intentionally managed elsewhere.");
        }

        foreach (var svc in candidates)
        {
            if (svc.DeclaredMounts is null)
                continue;

            var liveByDest = svc.LiveMounts.ToDictionary(m => m.Destination, StringComparer.Ordinal);

            foreach (var declared in svc.DeclaredMounts)
            {
                if (!liveByDest.TryGetValue(declared.Destination, out var live))
                {
                    yield return new Finding(
                        "mount-drift/missing-live", Severity.Red, svc.Name, svc.Host,
                        $"Declared mount '{declared.Source}' -> '{declared.Destination}' is not mounted in the running container.",
                        "The container is running without a declared mount — data may be written to the container layer and lost. Recreate the container from compose.");
                }
                else if (declared.Source.Length > 0
                         && !string.Equals(declared.Source, live.Source, StringComparison.Ordinal))
                {
                    yield return new Finding(
                        "mount-drift/source-mismatch", Severity.Red, svc.Name, svc.Host,
                        $"Mount at '{declared.Destination}': declared source '{declared.Source}' but live source is '{live.Source}'.",
                        "Declared and live state diverged — likely an edited compose file without a container recreate. Backups may target the wrong path. Recreate from compose.");
                }
            }

            var declaredDests = svc.DeclaredMounts.Select(m => m.Destination).ToHashSet(StringComparer.Ordinal);
            foreach (var live in svc.LiveMounts.Where(m => !declaredDests.Contains(m.Destination)))
            {
                if (IsAnonymousVolume(live.Source))
                    continue;

                yield return new Finding(
                    "mount-drift/extra-live", Severity.Yellow, svc.Name, svc.Host,
                    $"Live mount '{live.Source}' -> '{live.Destination}' is not declared in the compose config.",
                    "The running container carries a mount its compose file doesn't declare — a recreate would silently drop it. Add it to compose or remove it.");
            }
        }
    }

    // Anonymous volumes (image VOLUME directives) surface live with a 64-hex name.
    private static bool IsAnonymousVolume(string source) =>
        source.Length == 64 && source.All(char.IsAsciiHexDigitLower);
}
