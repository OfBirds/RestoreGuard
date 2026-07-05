using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers;

/// <summary>
/// Read-only client for one Docker host. Returns containers with declared vs. live
/// mounts — the mount-drift input. Lab hosts: .98, .55, .118 (see docs/homelab-map.md).
/// </summary>
public interface IDockerProvider
{
    Task<IReadOnlyList<Service>> GetServicesAsync(DockerHostConfig host, CancellationToken ct = default);
}
