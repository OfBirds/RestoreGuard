using RestoreGuard.Core.Model;

namespace RestoreGuard.Providers.Docker;

/// <summary>One Docker host to discover, with the docker binary path quirk per host.</summary>
public sealed record DockerHostConfig(string Alias, string DockerPath = "docker");

/// <summary>
/// Read-only Docker discovery over SSH: inspect every container, then resolve each
/// compose project's declared config via `docker compose config` on the host (which
/// re-resolves env exactly as deploy time did). A project that can't be resolved
/// (e.g. Portainer keeps its dirs inside its own container) yields services with
/// DeclaredMounts = null — visible to checks, never a crash.
/// </summary>
public sealed class DockerProvider(ISshProvider ssh) : IDockerProvider
{
    public async Task<IReadOnlyList<Service>> GetServicesAsync(DockerHostConfig host, CancellationToken ct = default)
    {
        var docker = host.DockerPath;
        var inspect = await ssh.RunAsync(
            host.Alias,
            $"ids=$({docker} ps -aq); [ -z \"$ids\" ] && echo '[]' || {docker} inspect $ids",
            ct);
        if (inspect.ExitCode != 0)
            throw new ProviderException($"docker inspect on {host.Alias} failed: {Truncate(inspect.StdErr)}");

        var containers = DockerInspectParser.Parse(inspect.StdOut);

        var projects = new Dictionary<string, ComposeProjectConfig?>(StringComparer.Ordinal);
        foreach (var c in containers)
        {
            if (c.ComposeProject is null || projects.ContainsKey(c.ComposeProject))
                continue;
            projects[c.ComposeProject] = await TryResolveProjectAsync(host, c, ct);
        }

        return ServiceAssembler.Build(host.Alias, containers, projects);
    }

    private async Task<ComposeProjectConfig?> TryResolveProjectAsync(
        DockerHostConfig host, InspectedContainer c, CancellationToken ct)
    {
        if (c.ComposeWorkingDir is null || c.ComposeConfigFiles is null)
            return null;

        var files = string.Join(" ", c.ComposeConfigFiles.Split(',').Select(f => $"-f {Quote(f)}"));
        var envFile = c.ComposeEnvironmentFile is null ? "" : $"--env-file {Quote(c.ComposeEnvironmentFile)} ";
        // -p is required: without it compose derives the project name from the directory
        // (e.g. Portainer's "495"), which breaks canonical volume-name resolution.
        var compose = $"cd {Quote(c.ComposeWorkingDir)} && {host.DockerPath} compose -p {Quote(c.ComposeProject!)} {envFile}{files}";

        var result = await ssh.RunAsync(host.Alias, $"{compose} config --format json", ct);
        if (result.ExitCode != 0)
            return null;

        ComposeProjectConfig config;
        try
        {
            config = ComposeConfigParser.Parse(result.StdOut);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        // Current per-service config hashes, compared against each container's
        // creation-time config-hash label to detect stale (edited-but-not-applied)
        // compose files. Hash failure degrades to "no hash", never kills resolution.
        var hashes = await ssh.RunAsync(host.Alias, $"{compose} config --hash '*'", ct);
        return hashes.ExitCode == 0
            ? config with { ServiceHashes = ComposeConfigParser.ParseHashes(hashes.StdOut) }
            : config;
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string Truncate(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}

public sealed class ProviderException(string message) : Exception(message);
