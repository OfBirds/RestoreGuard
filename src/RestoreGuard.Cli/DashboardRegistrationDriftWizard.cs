// DashboardRegistrationDriftWizard.cs
// Wizard section for configuring the dashboard-registration-drift check.
// Integrates with InteractiveMode.cs patterns (WizardIO, AskProbedAsync, AskSshDestinationAsync).

using System.Text.Json;
using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers;
using RestoreGuard.Providers.Dashboard;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Cli;

/// <summary>
/// Configures the dashboard-registration-drift check: verifies running Docker
/// containers are registered in the Homepage navigation dashboard.
/// </summary>
public static class DashboardRegistrationDriftWizard
{
    /// <summary>
    /// Called from RunWizardAsync after Docker hosts are configured, or from the
    /// menu loop as a standalone re-configure. Writes to the main config file.
    /// </summary>
    public static async Task ConfigureAsync(
        string configPath,
        ISshProvider ssh,
        WizardIO io,
        List<DockerHostConfig>? existingDockerHosts = null)
    {
        io.WriteLine();
        io.WriteLine("--- Dashboard registration drift (checks containers are on the dashboard) ---");
        io.WriteLine("This check verifies running Docker containers with published ports are");
        io.WriteLine("registered in the Homepage dashboard (services.yaml in a k3s ConfigMap).");

        if (!AskYesNo(io, "Enable this check?"))
            return;

        // Reuse Docker hosts already configured, or ask for them
        var dockerHosts = existingDockerHosts ?? new List<DockerHostConfig>();
        if (dockerHosts.Count == 0)
        {
            io.WriteLine("  No Docker hosts configured yet. Enter the hosts to scan:");
            while (true)
            {
                var alias = await AskSshDestinationAsync(ssh, io,
                    $"  Docker host #{dockerHosts.Count + 1} SSH destination (Enter = {(dockerHosts.Count == 0 ? "skip" : "done")})");
                if (alias.Length == 0)
                    break;
                dockerHosts.Add(new DockerHostConfig(alias, "docker"));
            }
        }
        else
        {
            io.WriteLine($"  Using {dockerHosts.Count} Docker host(s) already configured:");
            foreach (var h in dockerHosts)
                io.WriteLine($"    {h.Alias}");
            if (!AskYesNo(io, "  Use these for the dashboard check too?"))
            {
                dockerHosts = new List<DockerHostConfig>();
                while (true)
                {
                    var alias = await AskSshDestinationAsync(ssh, io,
                        $"  Docker host #{dockerHosts.Count + 1} SSH destination (Enter = done)");
                    if (alias.Length == 0)
                        break;
                    dockerHosts.Add(new DockerHostConfig(alias, "docker"));
                }
            }
        }

        // k3s master for ConfigMap access
        var k3sAlias = await AskSshDestinationAsync(ssh, io,
            "  k3s master SSH destination (for ConfigMap access, e.g. user@k3s-master; Enter = skip)");
        if (k3sAlias.Length == 0)
        {
            io.WriteLine("  Skipping dashboard check (no k3s master).");
            return;
        }

        // Live-probe: fetch ConfigMap and show registered services
        io.Write("  Fetching dashboard ConfigMap... ");
        var probeResult = await ssh.RunAsync(k3sAlias,
            "kubectl -n homepage get configmap homepage-config -o go-template='{{index .data \"services.yaml\"}}' 2>/dev/null || " +
            "k3s kubectl -n homepage get configmap homepage-config -o go-template='{{index .data \"services.yaml\"}}' 2>/dev/null");

        if (probeResult.ExitCode != 0)
        {
            io.WriteLine("PROBLEM — could not read ConfigMap homepage/homepage-config.");
            io.WriteLine("  Check: kubectl get cm/homepage-config -n homepage");
            if (!AskYesNo(io, "  Configure anyway (check will report RED on every audit)?"))
                return;
        }
        else
        {
            var servicesYaml = probeResult.StdOut;
            var parsed = DashboardProvider.ParseServicesFromYaml(servicesYaml);
            io.WriteLine($"found {parsed.Count} registered service(s).");

            // Show registered services
            io.WriteLine("  Registered services:");
            foreach (var svc in parsed)
                io.WriteLine($"    - {svc.Name}  {(svc.Url is { } u ? u : "")}");
        }

        // Live-probe: scan Docker hosts for running containers
        io.WriteLine();
        io.WriteLine("  Scanning Docker hosts for running containers...");
        var allContainers = new List<(string Host, string Name, string Ports)>();

        foreach (var host in dockerHosts)
        {
            var dockerResult = await ssh.RunAsync(host.Alias,
                $"{host.DockerPath} ps --format '{{{{.Names}}}}\\t{{{{.Ports}}}}' 2>/dev/null");

            if (dockerResult.ExitCode != 0)
            {
                io.WriteLine($"    {host.Alias}: PROBLEM — docker ps failed");
                continue;
            }

            var containers = dockerResult.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    var parts = line.Split('\t', 2);
                    return (Name: parts[0], Ports: parts.Length > 1 ? parts[1] : "");
                })
                .Where(c => c.Ports.Contains("->"))  // only containers with published ports
                .ToList();

            io.WriteLine($"    {host.Alias}: {containers.Count} container(s) with published ports");
            foreach (var c in containers)
            {
                allContainers.Add((host.Alias, c.Name, c.Ports));
                io.WriteLine($"      {c.Name}  {c.Ports}");
            }
        }

        var suppressions = new List<string>();

        // Ask about suppressions
        if (allContainers.Count > 0)
        {
            io.WriteLine();
            io.WriteLine("  Containers that are intentionally NOT on the dashboard can be suppressed.");
            io.WriteLine("  Format: host-alias/container-name (e.g. pve1/dozzle-agent)");
            while (true)
            {
                var entry = Ask(io, $"  Suppression #{suppressions.Count + 1} (Enter = {(suppressions.Count == 0 ? "none" : "done")})", "");
                if (entry.Length == 0)
                    break;
                suppressions.Add(entry);
            }

            if (suppressions.Count > 0)
            {
                io.WriteLine($"  {suppressions.Count} suppression(s) recorded.");
            }
        }

        // --- Save configuration ---
        var loaded = RestoreGuardConfig.LoadValidated(configPath);
        if (loaded is null) return;
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;

        var ddConfig = new DashboardDriftCliConfig(k3sAlias,
            dockerHosts.Select(h => h.Alias).ToList());
        var updated = loaded with { DashboardDrift = ddConfig };

        // Write suppressions
        if (suppressions.Count > 0)
        {
            var sf = updated.SuppressionsFile ?? "suppressions.json";
            var sp = Path.IsPathRooted(sf) ? sf : Path.Combine(configDir, sf);
            var existing = File.Exists(sp)
                ? JsonSerializer.Deserialize<List<Suppression>>(File.ReadAllText(sp), InteractiveMode.WizardJson) ?? []
                : [];
            foreach (var entry in suppressions)
            {
                var idx = entry.IndexOf('/');
                if (idx <= 0) continue;
                var alias = entry[..idx];
                var ctr = entry[(idx + 1)..];
                var key = ExtractIpFromAlias(alias);
                existing.Add(new Suppression(key, $"{key}/{ctr}",
                    "dashboard-registration-drift/unregistered",
                    "Suppressed during dashboard drift wizard setup.",
                    DateOnly.FromDateTime(DateTime.Today)));
            }
            File.WriteAllText(sp, JsonSerializer.Serialize(existing, InteractiveMode.WizardJson));
            if (updated.SuppressionsFile is null)
                updated = updated with { SuppressionsFile = sf };
        }

        File.WriteAllText(configPath, JsonSerializer.Serialize(updated, InteractiveMode.WizardJson));

        io.WriteLine();
        io.WriteLine($"  Dashboard drift check configured (k3s={k3sAlias}, {dockerHosts.Count} host(s)).");
        if (suppressions.Count > 0)
            io.WriteLine($"  {suppressions.Count} suppression(s) written.");
        io.WriteLine("  Run  d  (doctor) to verify.");
    }

    private static string ExtractIpFromAlias(string alias)
    {
        var at = alias.IndexOf('@');
        var h = at >= 0 ? alias[(at + 1)..] : alias;
        var colon = h.IndexOf(':');
        return colon >= 0 ? h[..colon] : h;
    }

    // Delegate to InteractiveMode's helpers
    private static Task<string> AskSshDestinationAsync(ISshProvider ssh, WizardIO io, string prompt)
        => InteractiveMode.AskSshDestinationAsync(ssh, io, prompt);

    private static string Ask(WizardIO io, string prompt, string defaultValue)
        => InteractiveMode.Ask(io, prompt, defaultValue);

    private static bool AskYesNo(WizardIO io, string prompt)
        => InteractiveMode.AskYesNo(io, prompt);
}
