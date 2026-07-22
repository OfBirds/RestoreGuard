namespace RestoreGuard.Core.Model;

/// <summary>
/// The unified inventory every provider feeds into and every check consumes.
/// This is the diff engine's input — the heart of the product. CapturedAt is the
/// only clock checks may use (freshness math), keeping rules golden-file testable.
/// </summary>
public sealed record LabInventory(
    DateTimeOffset CapturedAt,
    IReadOnlyList<Service> Services,
    IReadOnlyList<BackupArtifact> Backups,
    IReadOnlyList<StorageTarget> Storage)
{
    public static readonly LabInventory Empty = new(DateTimeOffset.MinValue, [], [], []);

    /// <summary>Homepage dashboard service registry state (for dashboard-registration-drift check).</summary>
    public DashboardRegistry Dashboard { get; init; } = new();

    /// <summary>Docker hosts with running containers (for dashboard-registration-drift check).</summary>
    public IReadOnlyList<DockerHost> Hosts { get; init; } = Array.Empty<DockerHost>();

    /// <summary>The k3s/master host identifier.</summary>
    public string ClusterHost { get; init; } = "";
}
