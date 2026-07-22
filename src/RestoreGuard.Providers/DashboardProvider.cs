// Provider that probes the Homepage dashboard ConfigMap and Docker hosts.
// Returns a combined result suitable for DashboardRegistrationDriftCheck.

using System.Text.RegularExpressions;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers.Dashboard;

/// <summary>
/// Probes the k3s master for the Homepage ConfigMap and each Docker host
/// for running containers with published ports.
/// </summary>
public sealed class DashboardProvider
{
    private readonly ISshProvider _ssh;

    public DashboardProvider(ISshProvider ssh) => _ssh = ssh;

    /// <summary>
    /// Combined result from a dashboard probe.  Errors are reported in-band so
    /// that partial data can still be fed to the check.
    /// </summary>
    public sealed record DashboardProbeResult
    {
        public DashboardRegistry Dashboard { get; init; } = new();
        public IReadOnlyList<DockerHost> Hosts { get; init; } = Array.Empty<DockerHost>();
        public string? Error { get; init; }
    }

    /// <summary>
    /// Runs the full probe.  Never throws; any failure is reflected in
    /// <see cref="DashboardProbeResult.Error"/>.
    /// </summary>
    public async Task<DashboardProbeResult> GetProbeAsync(
        string k3sMasterAlias,
        IReadOnlyList<DockerHostConfig> dockerHosts)
    {
        var errors = new List<string>();
        var dashboard = new DashboardRegistry { IsReachable = false };

        // 1. Fetch dashboard ConfigMap
        try
        {
            dashboard = await GetDashboardRegistryAsync(k3sMasterAlias);
        }
        catch (Exception ex)
        {
            errors.Add($"Dashboard ConfigMap probe failed: {ex.Message}");
        }

        // 2. Probe each Docker host
        var hosts = new List<DockerHost>();
        foreach (var hostCfg in dockerHosts)
        {
            try
            {
                var (host, error) = await GetDockerContainersAsync(hostCfg.Alias, hostCfg.DockerPath);
                if (error is not null) errors.Add(error);
                hosts.Add(host);
            }
            catch (Exception ex)
            {
                errors.Add($"Docker probe on {hostCfg.Alias} failed: {ex.Message}");
                // Add a host with no containers so structure is preserved
                hosts.Add(new DockerHost
                {
                    IpAddress = ExtractIpFromAlias(hostCfg.Alias),
                    Containers = Array.Empty<RunningContainer>()
                });
            }
        }

        return new DashboardProbeResult
        {
            Dashboard = dashboard,
            Hosts = hosts,
            Error = errors.Count > 0 ? string.Join("; ", errors) : null
        };
    }

    private async Task<DashboardRegistry> GetDashboardRegistryAsync(string k3sMasterAlias)
    {
        var cmd = "kubectl -n homepage get configmap homepage-config -o go-template='{{index .data \"services.yaml\"}}' 2>/dev/null || " +
                  "k3s kubectl -n homepage get configmap homepage-config -o go-template='{{index .data \"services.yaml\"}}' 2>/dev/null";
        var result = await _ssh.RunAsync(k3sMasterAlias, cmd);

        if (result.ExitCode != 0)
            return new DashboardRegistry { IsReachable = false };

        var services = ParseServicesFromYaml(result.StdOut);
        return new DashboardRegistry
        {
            IsReachable = true,
            Services = services
        };
    }

    public static List<DashboardService> ParseServicesFromYaml(string yaml)
    {
        var services = new List<DashboardService>();
        var lines = yaml.Split('\n');

        // Homepage services.yaml uses the format:
        //   - Group Name:
        //       - Service Name:
        //           href: http://...
        //           container: some-container   (optional)
        //
        // Match indented entries ("    - Something:") and walk sub-lines for
        // href + container. Skip known field-names so that href:/container:
        // lines at the same indent don't masquerade as entries.
        var serviceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "href", "server", "icon", "description", "siteMonitor", "widget", "container" };

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();

            // "    - ServiceName:" at 4-space indent
            if (!line.StartsWith("    - ") || !line.EndsWith(':') || line.StartsWith("        "))
                continue;

            var raw = line[6..^1].Trim(); // strip "    - " and trailing ":"
            if (raw.Length == 0 || serviceKeys.Contains(raw))
                continue;

            string? href = null;
            string? container = null;

            int j = i + 1;
            while (j < lines.Length && (lines[j].StartsWith("            ") || lines[j].StartsWith("        ")))
            {
                var sub = lines[j].TrimStart();
                if (sub.StartsWith("href:"))
                    href = sub[5..].Trim().Trim('"');
                else if (sub.StartsWith("container:"))
                    container = sub[10..].Trim().Trim('"');
                j++;
            }

            // Container name (lightweight docker-proxy integration) is the
            // stronger match; fall back to the YAML entry name.
            var effectiveName = container ?? raw;
            services.Add(new DashboardService { Name = effectiveName, Url = href });
        }

        return services;
    }

    private static readonly Regex PublishedPortRegex = new(
        @"(?:\d+\.\d+\.\d+\.\d+:|\[::\]:)?(\d+)->(\d+)/(\w+)",
        RegexOptions.Compiled);

    private async Task<(DockerHost host, string? error)> GetDockerContainersAsync(
        string alias, string dockerPath)
    {
        var cmd = $"{dockerPath} ps --format '{{{{.Names}}}}\\t{{{{.Ports}}}}' 2>/dev/null";
        var result = await _ssh.RunAsync(alias, cmd);

        var ip = ExtractIpFromAlias(alias);

        if (result.ExitCode != 0)
            return (new DockerHost { IpAddress = ip, Containers = Array.Empty<RunningContainer>() },
                    $"docker ps failed on {alias}: {result.StdErr}");

        var containers = new List<RunningContainer>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 2);
            var name = parts[0];
            var portsStr = parts.Length > 1 ? parts[1] : "";
            containers.Add(new RunningContainer
            {
                Name = name,
                Ports = ParsePublishedPorts(portsStr)
            });
        }

        return (new DockerHost { IpAddress = ip, Containers = containers }, null);
    }

    private static List<PublishedPort> ParsePublishedPorts(string portsStr)
    {
        var ports = new List<PublishedPort>();
        if (string.IsNullOrWhiteSpace(portsStr)) return ports;

        var entries = portsStr.Split(',', StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            var match = PublishedPortRegex.Match(entry);
            if (!match.Success) continue;

            if (int.TryParse(match.Groups[1].Value, out int hostPort) &&
                int.TryParse(match.Groups[2].Value, out int containerPort))
            {
                ports.Add(new PublishedPort
                {
                    HostPort = hostPort,
                    ContainerPort = containerPort,
                    Protocol = match.Groups[3].Value
                });
            }
        }
        return ports;
    }

    private static string ExtractIpFromAlias(string alias)
    {
        var atIdx = alias.IndexOf('@');
        var host = atIdx >= 0 ? alias[(atIdx + 1)..] : alias;
        var colonIdx = host.IndexOf(':');
        return colonIdx >= 0 ? host[..colonIdx] : host;
    }
}
