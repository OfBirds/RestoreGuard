using System;
using System.Collections.Generic;
using System.Linq;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

/// <summary>
/// Verifies that running Docker containers with published ports are registered in the Homepage dashboard.
/// Catches the "forgot to register service" failure where containers run but aren't visible in the dashboard.
/// </summary>
public sealed class DashboardRegistrationDriftCheck : ICheck
{
    public string RuleId => "dashboard-registration-drift";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        // Red: dashboard unreachable
        if (!inventory.Dashboard.IsReachable)
        {
            yield return new Finding(
                "dashboard-registration-drift/unreachable", Severity.Red, "dashboard-registry", inventory.ClusterHost,
                "Homepage dashboard ConfigMap unavailable — cannot verify service registration.",
                "Check k3s connectivity and ConfigMap existence (`kubectl get cm/homepage-config -n homepage`). Suppression key: dashboard-registry");
            yield break;
        }

        var registeredServices = inventory.Dashboard.Services;

        // Yellow: unregistered containers
        foreach (var host in inventory.Hosts)
        {
            foreach (var container in host.Containers.Where(c => c.Ports.Any()))
            {
                bool isRegistered = IsRegistered(container, host, registeredServices);

                if (!isRegistered)
                {
                    // Host-scoped suppression key
                    var resource = $"{host.IpAddress}/{container.Name}";
                    yield return new Finding(
                        "dashboard-registration-drift/unregistered", Severity.Yellow, resource, host.IpAddress,
                        $"Container '{container.Name}' on {host.IpAddress} publishes ports but is not registered in the dashboard.",
                        "Add the service to services.yaml in the homepage-config ConfigMap, or suppress if intentionally unregistered.");
                }
            }
        }
    }

    private static bool IsRegistered(RunningContainer container, DockerHost host, IReadOnlyCollection<DashboardService> services)
    {
        // Exact match by container name (case-insensitive)
        if (services.Any(svc => 
            string.Equals(svc.Name, container.Name, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Match by host:port in service URL
        foreach (var port in container.Ports)
        {
            foreach (var svc in services)
            {
                var url = svc?.Url ?? "";
                
                // Parse URL to extract authority (host:port)
                if (TryGetHostPortFromUrl(url, out var ip, out var portNumber))
                {
                    // Direct match
                    if (string.Equals(ip, host.IpAddress, StringComparison.OrdinalIgnoreCase) && 
                        portNumber == port.HostPort)
                        return true;
                        
                    // Handle default ports — when a service URL omits the port (e.g. http://host/),
                    // the container must occupy the scheme's default port on the *host* side
                    // (HostPort), not just internally (ContainerPort). A container mapping
                    // 8080→80 with an href "http://host/" shouldn't match; the href must include
                    // the explicit port.
                    if (portNumber == 80 && port.HostPort == 80
                        && string.Equals(ip, host.IpAddress, StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (portNumber == 443 && port.HostPort == 443
                        && string.Equals(ip, host.IpAddress, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetHostPortFromUrl(string url, out string ip, out int port)
    {
        ip = null!;
        port = 0;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            ip = uri.Host;
            
            // Handle default ports based on scheme when no explicit port in URL
            port = uri.Port;
            if (port == 0)
            {
                port = uri.Scheme?.ToLowerInvariant() switch
                {
                    "http" => 80,
                    "https" => 443,
                    _ => 0
                };
            }
            
            return true;
        }
        catch
        {
            return false;
        }
    }
}