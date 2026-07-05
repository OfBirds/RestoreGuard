namespace RestoreGuard.Core.Model;

public enum ServiceKind
{
    Container,
    Vm,
    Lxc,
    Stack,
}

/// <summary>A mount as either declared (compose config) or live (container inspect).</summary>
public sealed record MountSpec(string Source, string Destination, bool ReadOnly);

/// <summary>
/// A logical unit the lab runs: container, VM/LXC guest, or compose stack.
/// Declared vs. live mounts is the seam the mount-drift check diffs.
/// DeclaredMounts is null when no declared spec could be resolved (not compose-managed,
/// or the compose project dir is unreachable) — distinct from "declared zero mounts".
/// </summary>
public sealed record Service(
    string Name,
    string Host,
    ServiceKind Kind,
    string State,
    IReadOnlyList<MountSpec>? DeclaredMounts,
    IReadOnlyList<MountSpec> LiveMounts,
    string? Image,
    string? ComposeProject = null,
    // Compose config hashes: Live = what the container was created from (label),
    // Declared = the current file's hash. A mismatch means the config file changed
    // after creation — the running container is stale vs. its declared config.
    string? ConfigHashLive = null,
    string? ConfigHashDeclared = null);
