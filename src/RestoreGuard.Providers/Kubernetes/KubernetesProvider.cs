using System.Text.Json;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers.Kubernetes;

public sealed record KubernetesClusterConfig(
    string Name,
    string Alias,
    string Kubectl = "kubectl",
    string? VeleroNamespace = "velero",
    double MaxBackupAgeHours = 26,
    bool CheckWorkloads = true);

public sealed record KubernetesNode(string Name, string State, string? Uid = null);
public sealed record KubernetesDeployment(string Namespace, string Name, int DesiredReplicas, int AvailableReplicas);
public sealed record KubernetesPersistentVolumeClaim(string Namespace, string Name, string Phase, string? VolumeName = null);
public sealed record KubernetesSchedule(string Namespace, string Name, string? LastBackup = null);
/// <summary>Read-only evidence that Velero's file-system backup path captured a pod volume.</summary>
public sealed record KubernetesPodVolumeBackup(
    string BackupName,
    string PodNamespace,
    string PodName,
    string Volume,
    string? Phase,
    string? PersistentVolumeClaim = null)
{
    public bool Completed => string.Equals(Phase, "Completed", StringComparison.OrdinalIgnoreCase);
}

public sealed record KubernetesPodVolumeClaim(string Namespace, string PodName, string Volume, string ClaimName);
public sealed record KubernetesVolumeSnapshot(string BackupName, string Namespace, string ClaimName, bool ReadyToUse);

/// <summary>Discovery output which is deliberately separate from the generic inventory model.</summary>
public sealed record KubernetesState(
    string ClusterIdentity,
    IReadOnlyList<KubernetesPersistentVolumeClaim> PersistentVolumeClaims,
    IReadOnlyList<KubernetesSchedule> Schedules,
    bool VeleroInstalled,
    IReadOnlyList<KubernetesPodVolumeBackup> PodVolumeBackups,
    IReadOnlyList<KubernetesVolumeSnapshot> VolumeSnapshots,
    DateTimeOffset CapturedAt);

public sealed record KubernetesInventory(
    IReadOnlyList<Service> Services,
    IReadOnlyList<BackupArtifact> Backups,
    KubernetesState State);

public enum KubernetesProviderErrorKind { Forbidden, Authentication, Transport, Malformed, Command }

public sealed class KubernetesProviderException : ProviderException
{
    public KubernetesProviderException(KubernetesProviderErrorKind kind, string message)
        : base(message) => Kind = kind;

    public KubernetesProviderException(KubernetesProviderErrorKind kind, string message, Exception inner)
        : base(message, inner) => Kind = kind;

    public KubernetesProviderErrorKind Kind { get; }
}

public sealed class KubernetesProvider(ISshProvider ssh, Action<string>? progress = null)
{
    public static IReadOnlyList<string> EnabledSurfaces(KubernetesClusterConfig config)
    {
        var surfaces = new List<string>();
        if (config.CheckWorkloads) surfaces.AddRange(["nodes", "deployments"]);
        if (config.CheckWorkloads || config.VeleroNamespace is not null) surfaces.Add("persistent-volume-claims");
        if (config.VeleroNamespace is not null) surfaces.AddRange(["velero-backups", "velero-schedules", "pod-volume-backups", "pods", "csi-volume-snapshots"]);
        return surfaces;
    }

    public async Task<KubernetesInventory> GetClusterAsync(
        KubernetesClusterConfig config, CancellationToken ct = default)
    {
        var services = new List<Service>();
        var claims = new List<KubernetesPersistentVolumeClaim>();
        var schedules = new List<KubernetesSchedule>();
        var backups = new List<BackupArtifact>();
        var podVolumeBackups = new List<KubernetesPodVolumeBackup>();
        var volumeSnapshots = new List<KubernetesVolumeSnapshot>();
        var veleroInstalled = false;

        if (config.CheckWorkloads)
        {
            var nodesJson = await RunJsonAsync(config, "get nodes -o json", ct);
            foreach (var node in Parse("nodes", () => KubectlParser.ParseNodes(nodesJson)))
                services.Add(new Service(node.Name, config.Name, ServiceKind.K8sNode, node.State, null, [], null));

            var deploymentsJson = await RunJsonAsync(config, "get deploy -A -o json", ct);
            services.AddRange(Parse("deployments", () => KubectlParser.ParseDeployments(deploymentsJson))
                .Select(d => new Service($"{d.Namespace}/{d.Name}", config.Name, ServiceKind.K8sWorkload,
                    d.AvailableReplicas >= d.DesiredReplicas ? "available" : "degraded", null, [], null)));
        }

        // Bound claims are data-protection inputs whenever Velero is enabled, even if
        // workload availability checks are disabled. Do not claim coverage without them.
        if (config.CheckWorkloads || config.VeleroNamespace is not null)
        {
            var claimsJson = await RunJsonAsync(config, "get pvc -A -o json", ct);
            claims.AddRange(Parse("persistent volume claims", () => KubectlParser.ParsePersistentVolumeClaims(claimsJson)));
            services.AddRange(claims.Select(pvc => new Service($"{pvc.Namespace}/{pvc.Name}", config.Name,
                ServiceKind.K8sPersistentVolumeClaim, pvc.Phase, null, [], null)));
        }

        if (config.VeleroNamespace is not null)
        {
            var backup = await RunVeleroAsync(config, $"get backups.velero.io -n {Quote(config.VeleroNamespace)} -o json", ct);
            if (backup.MissingResource)
                veleroInstalled = false;
            else
            {
                veleroInstalled = true;
                backups.AddRange(Parse("Velero backups", () => KubectlParser.ParseBackups(backup.Json!, config.Name)));
            }

            if (veleroInstalled)
            {
                var schedule = await RunVeleroAsync(config, $"get schedules.velero.io -n {Quote(config.VeleroNamespace)} -o json", ct);
                if (!schedule.MissingResource)
                    schedules.AddRange(Parse("Velero schedules", () => KubectlParser.ParseSchedules(schedule.Json!, config.VeleroNamespace)));

                var pvb = await RunVeleroAsync(config, $"get podvolumebackups.velero.io -n {Quote(config.VeleroNamespace)} -o json", ct);
                if (!pvb.MissingResource)
                {
                    var podsJson = await RunJsonAsync(config, "get pods -A -o json", ct);
                    var podClaims = Parse("pods", () => KubectlParser.ParsePodVolumeClaims(podsJson));
                    podVolumeBackups.AddRange(Parse("Velero pod-volume backups", () => KubectlParser.ParsePodVolumeBackups(pvb.Json!, config.VeleroNamespace))
                        .Select(pvbBackup => pvbBackup with
                        {
                            PersistentVolumeClaim = podClaims.FirstOrDefault(reference =>
                                reference.Namespace == pvbBackup.PodNamespace
                                && reference.PodName == pvbBackup.PodName
                                && reference.Volume == pvbBackup.Volume)?.ClaimName,
                        }));
                }

                var snapshots = await RunOptionalJsonAsync(config, "get volumesnapshots.snapshot.storage.k8s.io -A -o json", ct);
                if (snapshots is not null)
                    volumeSnapshots.AddRange(Parse("CSI volume snapshots", () => KubectlParser.ParseVolumeSnapshots(snapshots)));
            }
        }

        return new KubernetesInventory(services, backups,
            new KubernetesState(config.Name, claims, schedules, veleroInstalled, podVolumeBackups, volumeSnapshots, DateTimeOffset.UtcNow));
    }

    public Task<KubernetesInventory> GetAsync(KubernetesClusterConfig config, CancellationToken ct = default) =>
        GetClusterAsync(config, ct);

    private async Task<string> RunJsonAsync(KubernetesClusterConfig config, string args, CancellationToken ct)
    {
        var command = $"{config.Kubectl} {args}";
        progress?.Invoke($"  ... [k8s] {config.Alias}: {Surface(args)}");
        SshResult result;
        try { result = await ssh.RunAsync(config.Alias, command, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new KubernetesProviderException(KubernetesProviderErrorKind.Transport, $"'{command}' on {config.Alias} failed.", ex); }
        if (result.ExitCode != 0) throw Classify(command, result.StdErr);
        try { using var _ = JsonDocument.Parse(result.StdOut); }
        catch (JsonException ex) { throw new KubernetesProviderException(KubernetesProviderErrorKind.Malformed, $"'{command}' returned malformed JSON.", ex); }
        return result.StdOut;
    }

    private async Task<(string? Json, bool MissingResource)> RunVeleroAsync(KubernetesClusterConfig config, string args, CancellationToken ct)
    {
        var command = $"{config.Kubectl} {args}";
        progress?.Invoke($"  ... [k8s] {config.Alias}: {Surface(args)}");
        SshResult result;
        try { result = await ssh.RunAsync(config.Alias, command, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new KubernetesProviderException(KubernetesProviderErrorKind.Transport, $"'{command}' on {config.Alias} failed.", ex); }
        if (result.ExitCode != 0)
        {
            var error = result.StdErr.Trim();
            if (IsMissingResource(error)) return (null, true);
            throw Classify(command, error);
        }
        try { using var _ = JsonDocument.Parse(result.StdOut); }
        catch (JsonException ex) { throw new KubernetesProviderException(KubernetesProviderErrorKind.Malformed, $"'{command}' returned malformed JSON.", ex); }
        return (result.StdOut, false);
    }

    private async Task<string?> RunOptionalJsonAsync(KubernetesClusterConfig config, string args, CancellationToken ct)
    {
        var command = $"{config.Kubectl} {args}";
        progress?.Invoke($"  ... [k8s] {config.Alias}: {Surface(args)}");
        SshResult result;
        try { result = await ssh.RunAsync(config.Alias, command, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new KubernetesProviderException(KubernetesProviderErrorKind.Transport, $"'{command}' on {config.Alias} failed.", ex); }
        if (result.ExitCode != 0)
        {
            if (IsMissingResource(result.StdErr)) return null;
            throw Classify(command, result.StdErr);
        }
        try { using var _ = JsonDocument.Parse(result.StdOut); }
        catch (JsonException ex) { throw new KubernetesProviderException(KubernetesProviderErrorKind.Malformed, $"'{command}' returned malformed JSON.", ex); }
        return result.StdOut;
    }

    private static bool IsMissingResource(string error) =>
        error.Contains("the server doesn't have a resource type", StringComparison.OrdinalIgnoreCase)
        || error.Contains("no matches for kind", StringComparison.OrdinalIgnoreCase);

    private static T Parse<T>(string surface, Func<T> parser)
    {
        try { return parser(); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        { throw new KubernetesProviderException(KubernetesProviderErrorKind.Malformed, $"Kubernetes {surface} JSON has an unexpected shape.", ex); }
    }

    private static KubernetesProviderException Classify(string command, string stderr)
    {
        var text = string.IsNullOrWhiteSpace(stderr) ? "command failed" : stderr.Trim().Split('\n')[0];
        var kind = text.Contains("forbidden", StringComparison.OrdinalIgnoreCase) || text.Contains("403", StringComparison.OrdinalIgnoreCase)
            ? KubernetesProviderErrorKind.Forbidden
            : text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || text.Contains("401", StringComparison.OrdinalIgnoreCase)
                ? KubernetesProviderErrorKind.Authentication : KubernetesProviderErrorKind.Command;
        return new KubernetesProviderException(kind, $"'{command}' failed: {text}");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string Surface(string args) => args switch
    {
        var value when value.StartsWith("get nodes", StringComparison.Ordinal) => "nodes",
        var value when value.StartsWith("get deploy", StringComparison.Ordinal) => "deployments",
        var value when value.StartsWith("get pvc", StringComparison.Ordinal) => "persistent-volume-claims",
        var value when value.StartsWith("get backups.velero.io", StringComparison.Ordinal) => "velero-backups",
        var value when value.StartsWith("get schedules.velero.io", StringComparison.Ordinal) => "velero-schedules",
        var value when value.StartsWith("get podvolumebackups.velero.io", StringComparison.Ordinal) => "pod-volume-backups",
        var value when value.StartsWith("get pods", StringComparison.Ordinal) => "pods",
        var value when value.StartsWith("get volumesnapshots.snapshot.storage.k8s.io", StringComparison.Ordinal) => "csi-volume-snapshots",
        _ => args,
    };
}
