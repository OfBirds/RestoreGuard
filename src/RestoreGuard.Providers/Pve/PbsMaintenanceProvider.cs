using System.Text.Json;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers.Pve;

public sealed record PbsMaintenanceConfig(
    string ExecAlias,
    int ContainerId,
    string Datastore,
    // proxmox-backup-client backup ids (usually hostnames) expected under
    // host/ in the datastore — bare-metal hosts backed up straight to PBS.
    IReadOnlyList<string>? HostBackups = null);

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
        var sync = await RunAsync(config, "proxmox-backup-manager sync-job list --output-format json", ct);
        // --all is required: without it PBS lists only RUNNING tasks, so a completed
        // verification would never be seen (found the hard way, live).
        var tasks = await RunAsync(config, "proxmox-backup-manager task list --all --limit 200 --output-format json", ct);

        // proxmox-backup-client host backups live under <datastore-path>/host/<id>/
        // with RFC3339 snapshot directories — a plain `ls` is the read-only probe.
        List<PbsHostBackup>? hostBackups = null;
        if (config.HostBackups is { Count: > 0 } ids)
        {
            var stores = await RunAsync(config, "proxmox-backup-manager datastore list --output-format json", ct);
            var path = ParseDatastorePath(stores, config.Datastore)
                ?? throw new ProviderException($"datastore '{config.Datastore}' not found in `datastore list`.");
            hostBackups = [];
            foreach (var id in ids)
            {
                // Exit code intentionally ignored: a missing host/<id> dir means
                // "never backed up", which is the check's finding, not an error.
                var listing = await ssh.RunAsync(config.ExecAlias,
                    $"pct exec {config.ContainerId} -- sh -c \"ls -1 '{path}/host/{id}' 2>/dev/null\"", ct);
                hostBackups.Add(new PbsHostBackup(id, ParseNewestHostSnapshot(listing.StdOut)));
            }
        }

        var (verifyLastRun, verifyLastStatus) = ParseLastTask(tasks, "verif");
        var (syncLastRun, syncLastStatus) = ParseLastTask(tasks, "sync");
        return new DatastoreMaintenance(
            config.Datastore,
            ParseGcLastRun(gc),
            ParseJobCount(verify),
            verifyLastRun,
            verifyLastStatus,
            ParseJobCount(sync),
            syncLastRun,
            syncLastStatus,
            hostBackups);
    }

    /// <summary>The datastore's on-disk path from `datastore list` (null = unknown name).</summary>
    public static string? ParseDatastorePath(string json, string datastore)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("name", out var n) && n.GetString() == datastore)
                return el.TryGetProperty("path", out var p) ? p.GetString() : null;
        }
        return null;
    }

    /// <summary>Snapshot dirs are RFC3339 timestamps ("2026-07-05T01:00:03Z");
    /// the newest wins. Anything unparseable (a stray file) is ignored.</summary>
    public static DateTimeOffset? ParseNewestHostSnapshot(string listing)
    {
        DateTimeOffset? best = null;
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateTimeOffset.TryParse(line, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var ts) && (best is null || ts > best))
            {
                best = ts;
            }
        }
        return best;
    }

    /// <summary>Newest COMPLETED task whose worker_type contains the given marker
    /// ("verif" → verificationjob, "sync" → syncjob; running ones have no endtime
    /// yet). Status is "OK" or PBS's error summary text.</summary>
    public static (DateTimeOffset? Time, string? Status) ParseLastTask(string json, string workerTypeMarker)
    {
        using var doc = JsonDocument.Parse(json);
        DateTimeOffset? best = null;
        string? status = null;

        foreach (var t in doc.RootElement.EnumerateArray())
        {
            if (!t.TryGetProperty("worker_type", out var wt)
                || wt.GetString()?.Contains(workerTypeMarker, StringComparison.OrdinalIgnoreCase) != true)
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

    /// <summary>Kept for existing callers/tests: the verify flavor of ParseLastTask.</summary>
    public static (DateTimeOffset? Time, string? Status) ParseLastVerify(string json) =>
        ParseLastTask(json, "verif");

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

    public static int ParseJobCount(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
    }

    /// <summary>Kept for existing callers/tests: verify-job flavor of ParseJobCount.</summary>
    public static int ParseVerifyJobCount(string json) => ParseJobCount(json);

    private async Task<string> RunAsync(PbsMaintenanceConfig config, string command, CancellationToken ct)
    {
        var result = await ssh.RunAsync(config.ExecAlias, $"pct exec {config.ContainerId} -- {command}", ct);
        if (result.ExitCode != 0)
            throw new ProviderException($"'{command}' in CT{config.ContainerId} via {config.ExecAlias} failed: {result.StdErr.Trim()}");
        return result.StdOut;
    }
}
