using System.Text.Json;

namespace RestoreGuard.Providers.Docker;

/// <summary>Resolved output of `docker compose config --format json` for one project.</summary>
public sealed record ComposeProjectConfig(
    string Name,
    IReadOnlyDictionary<string, ComposeServiceConfig> Services,
    IReadOnlyDictionary<string, string> VolumeNames,
    IReadOnlyDictionary<string, string>? ServiceHashes = null)
{
    /// <summary>Canonical live volume name for a declared volume source key.</summary>
    public string ResolveVolumeName(string sourceKey) =>
        VolumeNames.TryGetValue(sourceKey, out var name) ? name : $"{Name}_{sourceKey}";
}

public sealed record ComposeServiceConfig(string? Image, IReadOnlyList<DeclaredMount> Volumes);

/// <summary>A mount as declared in compose: type is "bind", "volume", or "tmpfs".</summary>
public sealed record DeclaredMount(string Type, string? Source, string Target, bool ReadOnly);

public static class ComposeConfigParser
{
    public static ComposeProjectConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var name = root.GetProperty("name").GetString() ?? "";

        var volumeNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("volumes", out var volsEl) && volsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var v in volsEl.EnumerateObject())
            {
                var canonical = v.Value.TryGetProperty("name", out var n) ? n.GetString() : null;
                volumeNames[v.Name] = canonical ?? $"{name}_{v.Name}";
            }
        }

        var services = new Dictionary<string, ComposeServiceConfig>(StringComparer.Ordinal);
        if (root.TryGetProperty("services", out var svcsEl) && svcsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var svc in svcsEl.EnumerateObject())
            {
                var image = svc.Value.TryGetProperty("image", out var img) ? img.GetString() : null;

                var mounts = new List<DeclaredMount>();
                if (svc.Value.TryGetProperty("volumes", out var mountsEl) && mountsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mountsEl.EnumerateArray())
                    {
                        mounts.Add(new DeclaredMount(
                            Type: m.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                            Source: m.TryGetProperty("source", out var s) ? s.GetString() : null,
                            Target: m.GetProperty("target").GetString() ?? "",
                            ReadOnly: m.TryGetProperty("read_only", out var ro) && ro.GetBoolean()));
                    }
                }

                services[svc.Name] = new ComposeServiceConfig(image, mounts);
            }
        }

        return new ComposeProjectConfig(name, services, volumeNames);
    }

    /// <summary>Parses `docker compose config --hash "*"` output: one "service hash" per line.</summary>
    public static IReadOnlyDictionary<string, string> ParseHashes(string output)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
                hashes[parts[0]] = parts[1];
        }
        return hashes;
    }
}
