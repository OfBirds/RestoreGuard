namespace RestoreGuard.Core.Model;

/// <summary>A port published from a running container to the host.</summary>
public sealed record PublishedPort
{
    public int HostPort { get; init; }
    public int ContainerPort { get; init; }
    public string Protocol { get; init; } = "tcp";
}

/// <summary>A running Docker container on a host, with its published ports.</summary>
public sealed record RunningContainer
{
    public string Name { get; init; } = "";
    public IReadOnlyList<PublishedPort> Ports { get; init; } = Array.Empty<PublishedPort>();
}

/// <summary>A Docker host that runs containers.</summary>
public sealed record DockerHost
{
    public string IpAddress { get; init; } = "";
    public IReadOnlyList<RunningContainer> Containers { get; init; } = Array.Empty<RunningContainer>();
}

/// <summary>A service registered in the Homepage dashboard.</summary>
public sealed record DashboardService
{
    public string Name { get; init; } = "";
    public string? Url { get; init; }
}

/// <summary>The state of the Homepage dashboard's service registry (ConfigMap).</summary>
public sealed record DashboardRegistry
{
    public bool IsReachable { get; init; }
    public IReadOnlyCollection<DashboardService> Services { get; init; } = Array.Empty<DashboardService>();
}
