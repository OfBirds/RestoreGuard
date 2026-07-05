using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

/// <summary>
/// Stale config files: the container carries the config-hash it was created from;
/// if the current compose file/env resolves to a different hash, the file was edited
/// without recreating the container. Mount drift catches the dangerous *effective*
/// divergence — this catches the whole class (env, image, ports…) before it bites,
/// including the "refactored compose, never applied" state where backups and reality
/// quietly disagree.
/// </summary>
public sealed class ConfigDriftCheck : ICheck
{
    public string RuleId => "config-drift";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        var candidates = inventory.Services.Where(s =>
            s.State == "running"
            && s.ConfigHashLive is not null
            && s.ConfigHashDeclared is not null);

        foreach (var svc in candidates)
        {
            if (!string.Equals(svc.ConfigHashLive, svc.ConfigHashDeclared, StringComparison.Ordinal))
            {
                yield return new Finding(
                    "config-drift/stale-config", Severity.Yellow, svc.Name, svc.Host,
                    $"Compose config of '{svc.ComposeProject}/{svc.Name}' changed after the container was created (config-hash {Short(svc.ConfigHashLive!)} live vs {Short(svc.ConfigHashDeclared!)} in the file) — the running container is stale vs. its declared config.",
                    "Apply the change (`docker compose up -d`) or revert the file; until then the file no longer describes reality and a recreate will surprise you.");
            }
        }
    }

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;
}
