using RestoreGuard.Core.Model;

namespace RestoreGuard.Providers.Docker;

/// <summary>
/// Joins live containers with resolved compose configs into Core Services.
/// Pure — no I/O — so the whole join is golden-file testable.
/// </summary>
public static class ServiceAssembler
{
    public static IReadOnlyList<Service> Build(
        string host,
        IReadOnlyList<InspectedContainer> containers,
        IReadOnlyDictionary<string, ComposeProjectConfig?> composeProjects)
    {
        var services = new List<Service>(containers.Count);

        foreach (var c in containers)
        {
            var live = c.Mounts
                .Select(m => new MountSpec(
                    Source: m.Type == "volume" ? m.Name ?? "" : m.Source ?? "",
                    Destination: m.Destination,
                    ReadOnly: !m.ReadWrite))
                .ToList();

            var project = c.ComposeProject is not null
                ? composeProjects.GetValueOrDefault(c.ComposeProject)
                : null;

            services.Add(new Service(
                Name: c.Name,
                Host: host,
                Kind: ServiceKind.Container,
                State: c.Status,
                DeclaredMounts: ResolveDeclared(c, composeProjects),
                LiveMounts: live,
                Image: c.Image,
                ComposeProject: c.ComposeProject,
                ConfigHashLive: c.Label("com.docker.compose.config-hash"),
                ConfigHashDeclared: c.ComposeService is not null && project?.ServiceHashes is { } hashes
                    ? hashes.GetValueOrDefault(c.ComposeService)
                    : null));
        }

        return services;
    }

    private static IReadOnlyList<MountSpec>? ResolveDeclared(
        InspectedContainer c,
        IReadOnlyDictionary<string, ComposeProjectConfig?> composeProjects)
    {
        if (c.ComposeProject is null || c.ComposeService is null)
            return null;
        if (!composeProjects.TryGetValue(c.ComposeProject, out var project) || project is null)
            return null;
        if (!project.Services.TryGetValue(c.ComposeService, out var svc))
            return null;

        return svc.Volumes
            .Where(m => m.Type is "bind" or "volume")
            .Select(m => new MountSpec(
                // Volume sources are declared by key but live as canonical names — resolve
                // here so the drift check compares like with like. Anonymous volumes have
                // no source; empty string means "any source acceptable".
                Source: m.Type == "volume"
                    ? (m.Source is null ? "" : project.ResolveVolumeName(m.Source))
                    : m.Source ?? "",
                Destination: m.Target,
                ReadOnly: m.ReadOnly))
            .ToList();
    }
}
