using System.Text.Json;
using System.Text.RegularExpressions;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers.Offsite;

public sealed record PbsOffsiteConfig(
    string Alias,
    string LogPath,
    string RcloneRemote,
    string TargetName);

/// <summary>
/// Off-site tier for a PBS datastore synced by a wrapper script that logs each
/// run ("=== &lt;ts&gt; sync start ===" … "=== sync finished rc=N ==="), with
/// `rclone about` reporting the remote's occupancy against
/// StorageCapacityCheck's thresholds.
/// </summary>
public sealed partial class PbsOffsiteProvider(ISshProvider ssh)
{
    public sealed record OffsiteState(BackupArtifact? LastSync, StorageTarget Remote);

    public async Task<OffsiteState> GetAsync(PbsOffsiteConfig config, CancellationToken ct = default)
    {
        var log = await RunAsync(config.Alias, $"tail -n 200 '{config.LogPath}'", ct);
        var about = await RunAsync(config.Alias, $"rclone about {config.RcloneRemote} --json", ct);

        var lastRun = ParseLastRun(log);
        var artifact = lastRun is null
            ? null
            : new BackupArtifact(
                Tier: BackupTier.CloudSync,
                TargetService: config.TargetName,
                Location: $"{config.RcloneRemote} (pbs-onedrive-sync.sh on {config.Alias})",
                Timestamp: lastRun.Value.Start,
                SizeBytes: 0,
                Method: "rclone-pbs-offsite",
                HasOffsiteCopy: true,
                Status: lastRun.Value.Rc == 0 ? "ok" : "failed");

        return new OffsiteState(artifact, ParseAbout(about, config));
    }

    [GeneratedRegex(@"^=== (?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}) \S+ sync start ===$")]
    private static partial Regex SyncStart();

    [GeneratedRegex(@"^=== sync finished rc=(?<rc>-?\d+) ===$")]
    private static partial Regex SyncFinished();

    /// <summary>Last start line and the rc that follows it. A start with no finish
    /// line yet (or ever — a killed run) reports rc = -1 so it counts as failed.</summary>
    public static (DateTimeOffset Start, int Rc)? ParseLastRun(string log)
    {
        DateTimeOffset? start = null;
        var rc = -1;
        foreach (var line in log.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (SyncStart().Match(line) is { Success: true } s)
            {
                // Log timestamps are host-local; treated as UTC (≤2h skew vs a 26h limit).
                if (DateTimeOffset.TryParseExact(s.Groups["ts"].Value, "yyyy-MM-dd HH:mm",
                        null, System.Globalization.DateTimeStyles.AssumeUniversal, out var ts))
                {
                    start = ts;
                    rc = -1;
                }
            }
            else if (start is not null && SyncFinished().Match(line) is { Success: true } f)
            {
                rc = int.Parse(f.Groups["rc"].Value);
            }
        }
        return start is null ? null : (start.Value, rc);
    }

    public static StorageTarget ParseAbout(string json, PbsOffsiteConfig config)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var total = root.TryGetProperty("total", out var t) ? t.GetInt64() : 0;
        var free = root.TryGetProperty("free", out var f)
            ? f.GetInt64()
            : total - (root.TryGetProperty("used", out var u) ? u.GetInt64() : 0);
        return new StorageTarget(config.RcloneRemote, config.Alias, total, free, "available", null);
    }

    private async Task<string> RunAsync(string alias, string command, CancellationToken ct)
    {
        var result = await ssh.RunAsync(alias, command, ct);
        if (result.ExitCode != 0)
            throw new ProviderException($"'{command}' on {alias} failed: {result.StdErr.Trim()}");
        return result.StdOut;
    }
}
