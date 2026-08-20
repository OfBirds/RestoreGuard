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

/// <summary>A generic rclone-style off-site sync job: a wrapper script that logs
/// "=== &lt;yyyy-MM-dd HH:mm&gt; ... sync start ===" / "=== sync finished rc=N ==="
/// lines. RcloneRemote is optional — set it to also watch the remote's capacity
/// (`rclone about`; not every backend supports it).</summary>
public sealed record OffsiteJobConfig(
    string Name,
    string Alias,
    string LogPath,
    string? RcloneRemote = null,
    double MaxSyncAgeHours = 26,
    double MaxRunAgeHours = 6);

/// <summary>
/// Off-site tier for a PBS datastore synced by a wrapper script that logs each
/// run ("=== &lt;ts&gt; sync start ===" … "=== sync finished rc=N ==="), with
/// `rclone about` reporting the remote's occupancy against
/// StorageCapacityCheck's thresholds.
/// </summary>
public sealed partial class PbsOffsiteProvider(ISshProvider ssh)
{
    public sealed record OffsiteState(BackupArtifact? LastSync, BackupArtifact? ActiveSync, StorageTarget? Remote, string? RemoteError);

    /// <summary>Generic-job state: LastSync is null when the log has no runs at all
    /// (the check turns that into offsite/never-ran); Remote is null when no
    /// rcloneRemote was configured.</summary>
    public sealed record OffsiteJobState(BackupArtifact? LastSync, BackupArtifact? ActiveSync, StorageTarget? Remote, string? RemoteError);

    public sealed record ParsedRuns((DateTimeOffset Start, int Rc)? LastCompleted, DateTimeOffset? ActiveStart);

    /// <summary>The generic flavor of <see cref="GetAsync"/>: same log contract,
    /// job identified by its configured name, capacity probe only when a remote
    /// is configured.</summary>
    public async Task<OffsiteJobState> GetJobAsync(OffsiteJobConfig config, CancellationToken ct = default)
    {
        var log = await RunAsync(config.Alias, LogCommand(config.LogPath), ct);

        var runs = ParseRuns(log);
        var artifact = runs.LastCompleted is null
            ? null
            : new BackupArtifact(
                Tier: BackupTier.CloudSync,
                TargetService: config.Name,
                Location: $"{config.RcloneRemote ?? config.LogPath} ({config.Name} on {config.Alias})",
                Timestamp: runs.LastCompleted.Value.Start,
                SizeBytes: 0,
                Method: "rclone-offsite",
                HasOffsiteCopy: true,
                Status: runs.LastCompleted.Value.Rc == 0 ? "ok" : "failed");
        var active = runs.ActiveStart is null
            ? null
            : new BackupArtifact(
                Tier: BackupTier.CloudSync,
                TargetService: config.Name,
                Location: $"{config.RcloneRemote ?? config.LogPath} ({config.Name} on {config.Alias})",
                Timestamp: runs.ActiveStart.Value,
                SizeBytes: 0,
                Method: "rclone-offsite",
                HasOffsiteCopy: true,
                Status: "running");

        StorageTarget? remote = null;
        string? remoteError = null;
        if (config.RcloneRemote is { Length: > 0 } r)
        {
            (remote, remoteError) = await GetRemoteAsync(config.Alias, r,
                new PbsOffsiteConfig(config.Alias, config.LogPath, r, config.Name), ct);
        }

        return new OffsiteJobState(artifact, active, remote, remoteError);
    }

    public async Task<OffsiteState> GetAsync(PbsOffsiteConfig config, CancellationToken ct = default)
    {
        var log = await RunAsync(config.Alias, LogCommand(config.LogPath), ct);
        var (remote, remoteError) = await GetRemoteAsync(config.Alias, config.RcloneRemote, config, ct);

        var runs = ParseRuns(log);
        var artifact = runs.LastCompleted is null
            ? null
            : new BackupArtifact(
                Tier: BackupTier.CloudSync,
                TargetService: config.TargetName,
                Location: $"{config.RcloneRemote} (pbs-onedrive-sync.sh on {config.Alias})",
                Timestamp: runs.LastCompleted.Value.Start,
                SizeBytes: 0,
                Method: "rclone-pbs-offsite",
                HasOffsiteCopy: true,
                Status: runs.LastCompleted.Value.Rc == 0 ? "ok" : "failed");
        var active = runs.ActiveStart is null
            ? null
            : new BackupArtifact(
                Tier: BackupTier.CloudSync,
                TargetService: config.TargetName,
                Location: $"{config.RcloneRemote} (pbs-onedrive-sync.sh on {config.Alias})",
                Timestamp: runs.ActiveStart.Value,
                SizeBytes: 0,
                Method: "rclone-pbs-offsite",
                HasOffsiteCopy: true,
                Status: "running");

        return new OffsiteState(artifact, active, remote, remoteError);
    }

    [GeneratedRegex(@"^=== (?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}) (?<zone>\S+) sync start ===$")]
    private static partial Regex SyncStart();

    [GeneratedRegex(@"^=== sync finished rc=(?<rc>-?\d+) ===$")]
    private static partial Regex SyncFinished();

    [GeneratedRegex(@"^=== restoreguard timezone (?<offset>[+-]\d{2}:\d{2}) ===$", RegexOptions.Multiline)]
    private static partial Regex RestoreGuardTimezone();

    /// <summary>Returns both the last completed run and, independently, any current
    /// start without a completion marker. A running job must not erase a prior success.
    /// Checks enforce the configured maximum running duration so a hung job stays loud.</summary>
    public static ParsedRuns ParseRuns(string log)
    {
        var observedOffset = GetObservedOffset(log);
        DateTimeOffset? active = null;
        var activeCompleted = false;
        (DateTimeOffset Start, int Rc)? completed = null;
        foreach (var line in log.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (SyncStart().Match(line) is { Success: true } s)
            {
                if (TryParseStart(s, observedOffset, out var start))
                {
                    active = start;
                    activeCompleted = false;
                }
                else
                {
                    active = null;
                    activeCompleted = true;
                }
            }
            else if (active is not null && SyncFinished().Match(line) is { Success: true } f)
            {
                completed = (active.Value, int.Parse(f.Groups["rc"].Value));
                activeCompleted = true;
            }
        }
        return new ParsedRuns(completed, activeCompleted ? null : active);
    }

    /// <summary>Compatibility helper for callers that need only the last completed run.</summary>
    public static (DateTimeOffset Start, int Rc)? ParseLastRun(string log)
        => ParseRuns(log).LastCompleted;

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

    private static string LogCommand(string logPath) =>
        $"if test -r '{logPath}'; then (grep -E '^=== ' '{logPath}' || [ $? -eq 1 ]) | tail -n 200; else exit 2; fi; printf '\n=== restoreguard timezone %s ===\n' \"$(date +%:z)\"";

    private async Task<(StorageTarget? Remote, string? Error)> GetRemoteAsync(
        string alias, string rcloneRemote, PbsOffsiteConfig config, CancellationToken ct)
    {
        var command = $"rclone about {rcloneRemote} --json";
        var result = await ssh.RunAsync(alias, command, ct);
        if (result.ExitCode != 0)
            return (null, $"'{command}' on {alias} failed: {result.StdErr.Trim()}");
        try
        {
            return (ParseAbout(result.StdOut, config), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, $"'{command}' on {alias} returned invalid JSON: {ex.Message}");
        }
    }

    private static TimeSpan GetObservedOffset(string log)
    {
        var marker = RestoreGuardTimezone().Match(log);
        if (marker.Success && DateTimeOffset.TryParseExact(
                $"2000-01-01 {marker.Groups["offset"].Value}", "yyyy-MM-dd zzz",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var observed))
            return observed.Offset;
        return TimeSpan.Zero;
    }

    private static bool TryParseStart(Match match, TimeSpan observedOffset, out DateTimeOffset start)
    {
        var timestamp = match.Groups["ts"].Value;
        var zone = match.Groups["zone"].Value;
        if (DateTimeOffset.TryParseExact($"{timestamp} {zone}", "yyyy-MM-dd HH:mm zzz",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out start))
        {
            start = start.ToUniversalTime();
            return true;
        }

        if (DateTime.TryParseExact(timestamp, "yyyy-MM-dd HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var local))
        {
            start = new DateTimeOffset(local, observedOffset).ToUniversalTime();
            return true;
        }

        start = default;
        return false;
    }
}
