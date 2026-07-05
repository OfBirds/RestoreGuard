using System.Text.Json;
using System.Text.RegularExpressions;

namespace RestoreGuard.Providers.TrueNas;

public sealed record TrueNasPool(
    string Name, string Status, DateTimeOffset? LastScrub, long ScrubErrors);

public sealed record TrueNasDataset(string Id, long UsedBytes, long AvailableBytes);

public sealed record CloudSyncTask(
    long Id,
    string Description,
    string Path,
    string Direction,
    bool Enabled,
    string? JobState,
    DateTimeOffset? TimeFinished,
    string? DestinationFolder);

public static partial class TrueNasParsers
{
    public static IReadOnlyList<TrueNasPool> ParsePools(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(el =>
        {
            var hasScan = el.TryGetProperty("scan", out var scan) && scan.ValueKind == JsonValueKind.Object;
            var isScrub = hasScan
                && scan.TryGetProperty("function", out var fn) && fn.GetString() == "SCRUB";
            return new TrueNasPool(
                Name: el.GetProperty("name").GetString() ?? "",
                Status: el.GetProperty("status").GetString() ?? "UNKNOWN",
                LastScrub: isScrub ? MiddlewareJson.GetDate(scan, "end_time") : null,
                ScrubErrors: isScrub && scan.TryGetProperty("errors", out var err) ? err.GetInt64() : 0);
        }).ToList();
    }

    public static IReadOnlyList<TrueNasDataset> ParseDatasets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(el => new TrueNasDataset(
            Id: el.GetProperty("id").GetString() ?? "",
            UsedBytes: MiddlewareJson.GetParsedSize(el, "used"),
            AvailableBytes: MiddlewareJson.GetParsedSize(el, "available"))).ToList();
    }

    [GeneratedRegex(@"^(?<ds>[^@]+)@auto-(?<ts>\d{4}-\d{2}-\d{2}_\d{2}-\d{2})$")]
    private static partial Regex AutoSnapshotName();

    /// <summary>
    /// Latest auto-snapshot per dataset, parsed from snapshot names
    /// (dataset@auto-yyyy-MM-dd_HH-mm; timestamps are appliance-local, treated as UTC —
    /// the ≤2h skew is well inside the 26h freshness margin). Manual and boot-env
    /// snapshots don't match the pattern and are ignored.
    /// </summary>
    public static IReadOnlyDictionary<string, (DateTimeOffset Latest, string SnapshotName)> ParseLatestAutoSnapshots(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var latest = new Dictionary<string, (DateTimeOffset, string)>(StringComparer.Ordinal);

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = el.GetProperty("name").GetString() ?? "";
            var m = AutoSnapshotName().Match(name);
            if (!m.Success)
                continue;
            if (!DateTimeOffset.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd_HH-mm",
                    null, System.Globalization.DateTimeStyles.AssumeUniversal, out var ts))
                continue;

            var ds = m.Groups["ds"].Value;
            if (!latest.TryGetValue(ds, out var cur) || ts > cur.Item1)
                latest[ds] = (ts, name);
        }

        return latest;
    }

    public static IReadOnlyList<CloudSyncTask> ParseCloudSyncTasks(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(el =>
        {
            var hasJob = el.TryGetProperty("job", out var job) && job.ValueKind == JsonValueKind.Object;
            return new CloudSyncTask(
                Id: el.GetProperty("id").GetInt64(),
                Description: el.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                Path: el.GetProperty("path").GetString() ?? "",
                Direction: el.GetProperty("direction").GetString() ?? "",
                Enabled: el.TryGetProperty("enabled", out var en) && en.GetBoolean(),
                JobState: hasJob && job.TryGetProperty("state", out var st) ? st.GetString() : null,
                TimeFinished: hasJob ? MiddlewareJson.GetDate(job, "time_finished") : null,
                DestinationFolder: el.TryGetProperty("attributes", out var attrs)
                    && attrs.ValueKind == JsonValueKind.Object
                    && attrs.TryGetProperty("folder", out var f) ? f.GetString() : null);
        }).ToList();
    }
}
