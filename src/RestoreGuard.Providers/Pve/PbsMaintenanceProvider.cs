using System.Text.Json;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers.Pve;

public sealed record PbsMaintenanceConfig(string ExecAlias, int ContainerId, string Datastore);

/// <summary>
/// Reads PBS maintenance state by running proxmox-backup-manager inside the PBS LXC
/// (`pct exec` from its PVE host) — read-only status queries; no PBS API token needed.
/// </summary>
public sealed class PbsMaintenanceProvider(ISshProvider ssh)
{
    public async Task<DatastoreMaintenance> GetAsync(PbsMaintenanceConfig config, CancellationToken ct = default)
    {
        var gc = await RunAsync(config,
            $"proxmox-backup-manager garbage-collection status {config.Datastore} --output-format json", ct);
        var verify = await RunAsync(config, "proxmox-backup-manager verify-job list --output-format json", ct);
        // --all is required: without it PBS lists only RUNNING tasks, so a completed
        // verification would never be seen (found the hard way, live).
        var tasks = await RunAsync(config, "proxmox-backup-manager task list --all --limit 200 --output-format json", ct);

        var (verifyLastRun, verifyLastStatus) = ParseLastVerify(tasks);
        return new DatastoreMaintenance(
            config.Datastore,
            ParseGcLastRun(gc),
            ParseVerifyJobCount(verify),
            verifyLastRun,
            verifyLastStatus);
    }

    /// <summary>Newest COMPLETED verification task (running ones have no endtime yet).
    /// Status is "OK" or PBS's error summary text.</summary>
    public static (DateTimeOffset? Time, string? Status) ParseLastVerify(string json)
    {
        using var doc = JsonDocument.Parse(json);
        DateTimeOffset? best = null;
        string? status = null;

        foreach (var t in doc.RootElement.EnumerateArray())
        {
            if (!t.TryGetProperty("worker_type", out var wt)
                || wt.GetString()?.Contains("verif", StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }
            if (!t.TryGetProperty("endtime", out var end) || end.ValueKind != JsonValueKind.Number)
                continue; // still running

            var time = DateTimeOffset.FromUnixTimeSeconds(end.GetInt64());
            if (best is null || time > best)
            {
                best = time;
                status = t.TryGetProperty("status", out var st) ? st.GetString() : null;
            }
        }
        return (best, status);
    }

    /// <summary>GC status carries no timestamp field, but its UPID encodes the start
    /// time as the hex field at index 5 (UPID:node:pid:pstart:task:starttime:…).
    /// A null upid means GC has never run on this datastore.</summary>
    public static DateTimeOffset? ParseGcLastRun(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("upid", out var upidEl) || upidEl.ValueKind != JsonValueKind.String)
            return null;

        var parts = (upidEl.GetString() ?? "").Split(':');
        if (parts.Length < 6 || !long.TryParse(parts[5],
                System.Globalization.NumberStyles.HexNumber, null, out var epoch))
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(epoch);
    }

    public static int ParseVerifyJobCount(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
    }

    private async Task<string> RunAsync(PbsMaintenanceConfig config, string command, CancellationToken ct)
    {
        var result = await ssh.RunAsync(config.ExecAlias, $"pct exec {config.ContainerId} -- {command}", ct);
        if (result.ExitCode != 0)
            throw new ProviderException($"'{command}' in CT{config.ContainerId} via {config.ExecAlias} failed: {result.StdErr.Trim()}");
        return result.StdOut;
    }
}
