using System.Text.Json;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Providers.Kubernetes;

public static class KubectlParser
{
    public static IReadOnlyList<KubernetesNode> ParseNodes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Items(doc).Select(x => new KubernetesNode(
            x.GetProperty("metadata").GetProperty("name").GetString() ?? "",
            x.GetProperty("status").TryGetProperty("conditions", out var conditions) && conditions.EnumerateArray()
                .Where(c => c.TryGetProperty("type", out var type) && type.GetString() == "Ready")
                .Any(c => c.TryGetProperty("status", out var state) && state.GetString() == "True") ? "Ready" : "NotReady",
            x.GetProperty("metadata").TryGetProperty("uid", out var uid) ? uid.GetString() : null)).ToList();
    }

    public static IReadOnlyList<KubernetesDeployment> ParseDeployments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Items(doc).Select(x => new KubernetesDeployment(
            x.GetProperty("metadata").GetProperty("namespace").GetString() ?? "",
            x.GetProperty("metadata").GetProperty("name").GetString() ?? "",
            Number(x, "spec", "replicas", defaultValue: 1), Number(x, "status", "availableReplicas"))).ToList();
    }

    public static IReadOnlyList<KubernetesPersistentVolumeClaim> ParsePersistentVolumeClaims(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Items(doc).Select(x => new KubernetesPersistentVolumeClaim(
            x.GetProperty("metadata").GetProperty("namespace").GetString() ?? "",
            x.GetProperty("metadata").GetProperty("name").GetString() ?? "",
            x.GetProperty("status").TryGetProperty("phase", out var phase) ? phase.GetString() ?? "Unknown" : "Unknown",
            x.GetProperty("spec").TryGetProperty("volumeName", out var volume) ? volume.GetString() : null)).ToList();
    }

    public static IReadOnlyList<KubernetesSchedule> ParseSchedules(string json, string ns)
    {
        using var doc = JsonDocument.Parse(json);
        return Items(doc).Select(x => new KubernetesSchedule(ns, x.GetProperty("metadata").GetProperty("name").GetString() ?? "",
            x.GetProperty("status").TryGetProperty("lastBackup", out var last) ? last.GetString() : null)).ToList();
    }

    public static IReadOnlyList<BackupArtifact> ParseBackups(string json, string clusterIdentity)
    {
        using var doc = JsonDocument.Parse(json);
        return Items(doc).Select(x => {
            var status = x.TryGetProperty("status", out var s) ? s : default;
            var timestamp = ReadDate(status, "completionTimestamp") ?? ReadDate(x.GetProperty("metadata"), "creationTimestamp") ?? DateTimeOffset.MinValue;
            var phase = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("phase", out var p) ? p.GetString() : null;
            return new BackupArtifact(BackupTier.KubernetesBackup, $"{clusterIdentity} velero",
                x.GetProperty("metadata").GetProperty("name").GetString() ?? "", timestamp, 0, "velero", false, phase);
        }).ToList();
    }

    public static IReadOnlyList<KubernetesPodVolumeBackup> ParsePodVolumeBackups(string json, string fallbackNamespace)
    {
        using var doc = JsonDocument.Parse(json);
        return Items(doc).Select(x =>
        {
            var metadata = x.GetProperty("metadata");
            var spec = x.TryGetProperty("spec", out var s) ? s : default;
            var status = x.TryGetProperty("status", out var st) ? st : default;
            var backupName = Label(metadata, "velero.io/backup-name")
                ?? String(spec, "backupName")
                ?? "";
            var pod = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("pod", out var podValue)
                ? podValue : default;
            return new KubernetesPodVolumeBackup(
                backupName,
                String(pod, "namespace") ?? (metadata.TryGetProperty("namespace", out var ns) ? ns.GetString() ?? fallbackNamespace : fallbackNamespace),
                String(pod, "name") ?? "",
                String(spec, "volume") ?? "",
                String(status, "phase"));
        }).ToList();
    }

    public static IReadOnlyList<KubernetesPodVolumeClaim> ParsePodVolumeClaims(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Items(doc).SelectMany(pod =>
        {
            var metadata = pod.GetProperty("metadata");
            var podNamespace = metadata.GetProperty("namespace").GetString() ?? "";
            var podName = metadata.GetProperty("name").GetString() ?? "";
            if (!pod.TryGetProperty("spec", out var spec) || !spec.TryGetProperty("volumes", out var volumes)) return [];
            return volumes.EnumerateArray().Where(volume => volume.TryGetProperty("persistentVolumeClaim", out _)).Select(volume =>
                new KubernetesPodVolumeClaim(podNamespace, podName,
                    volume.GetProperty("name").GetString() ?? "",
                    volume.GetProperty("persistentVolumeClaim").GetProperty("claimName").GetString() ?? ""));
        }).ToList();
    }

    public static IReadOnlyList<KubernetesVolumeSnapshot> ParseVolumeSnapshots(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Items(doc).Select(snapshot =>
        {
            var metadata = snapshot.GetProperty("metadata");
            var source = snapshot.GetProperty("spec").GetProperty("source");
            var status = snapshot.TryGetProperty("status", out var candidate) ? candidate : default;
            return new KubernetesVolumeSnapshot(
                Label(metadata, "velero.io/backup-name") ?? "",
                metadata.GetProperty("namespace").GetString() ?? "",
                String(source, "persistentVolumeClaimName") ?? "",
                status.ValueKind == JsonValueKind.Object && status.TryGetProperty("readyToUse", out var ready) && ready.ValueKind == JsonValueKind.True);
        }).ToList();
    }

    private static IEnumerable<JsonElement> Items(JsonDocument doc) => doc.RootElement.GetProperty("items").EnumerateArray();
    private static int Number(JsonElement root, string parent, string property, int defaultValue = 0) => root.TryGetProperty(parent, out var p) ? Number(p, property, defaultValue) : defaultValue;
    private static int Number(JsonElement root, string property, int defaultValue = 0) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.TryGetInt32(out var n) ? n : defaultValue;
    private static string? String(JsonElement root, string property) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) ? value.GetString() : null;
    private static string? Label(JsonElement metadata, string label) => metadata.TryGetProperty("labels", out var labels)
        && labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty(label, out var value) ? value.GetString() : null;
    private static DateTimeOffset? ReadDate(JsonElement root, string property) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && DateTimeOffset.TryParse(value.GetString(), out var date) ? date : null;
}
