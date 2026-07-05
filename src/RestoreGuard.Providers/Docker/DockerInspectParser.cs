using System.Text.Json;

namespace RestoreGuard.Providers.Docker;

/// <summary>One container as reported by `docker inspect` (the subset RestoreGuard reads).</summary>
public sealed record InspectedContainer(
    string Name,
    string Status,
    string Image,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<LiveMount> Mounts)
{
    public string? Label(string key) => Labels.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public string? ComposeProject => Label("com.docker.compose.project");
    public string? ComposeService => Label("com.docker.compose.service");
    public string? ComposeWorkingDir => Label("com.docker.compose.project.working_dir");
    public string? ComposeConfigFiles => Label("com.docker.compose.project.config_files");
    public string? ComposeEnvironmentFile => Label("com.docker.compose.project.environment_file");
}

/// <summary>A live mount from inspect: volume mounts carry Name, bind mounts carry Source.</summary>
public sealed record LiveMount(string Type, string? Name, string? Source, string Destination, bool ReadWrite);

public static class DockerInspectParser
{
    public static IReadOnlyList<InspectedContainer> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<InspectedContainer>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = el.GetProperty("Name").GetString() ?? "";
            var status = el.GetProperty("State").GetProperty("Status").GetString() ?? "unknown";
            var config = el.GetProperty("Config");
            var image = config.GetProperty("Image").GetString() ?? "";

            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (config.TryGetProperty("Labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in labelsEl.EnumerateObject())
                    labels[p.Name] = p.Value.GetString() ?? "";
            }

            var mounts = new List<LiveMount>();
            if (el.TryGetProperty("Mounts", out var mountsEl) && mountsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mountsEl.EnumerateArray())
                {
                    mounts.Add(new LiveMount(
                        Type: m.TryGetProperty("Type", out var t) ? t.GetString() ?? "" : "",
                        Name: m.TryGetProperty("Name", out var n) ? n.GetString() : null,
                        Source: m.TryGetProperty("Source", out var s) ? s.GetString() : null,
                        Destination: m.GetProperty("Destination").GetString() ?? "",
                        ReadWrite: m.TryGetProperty("RW", out var rw) && rw.GetBoolean()));
                }
            }

            result.Add(new InspectedContainer(name.TrimStart('/'), status, image, labels, mounts));
        }

        return result;
    }
}
