using System.Globalization;
using System.Text.Json;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers.FileBackups;

/// <summary>One configured file-level backup source. Kind selects the adapter:
/// "restic" (repo + password file), "borg" (repo + passphrase file),
/// "kopia" (server-side connected repo — `kopia repository connect` once on the host),
/// "dir" (a directory of archive files), "haos" (Home Assistant OS backups via
/// the PVE qemu guest agent), "snapper" (btrfs snapshots by snapper config name).
/// CanaryPath (restic/borg only) opts into the restore canary: a small file that
/// must be restorable from the LATEST snapshot — proof the repo actually restores,
/// not just that snapshots exist.</summary>
public sealed record FileBackupSource(
    string Name,
    string Kind,
    string Alias,
    string? Repo = null,
    string? PasswordFile = null,
    string? Path = null,
    int? Vmid = null,
    string? SnapperConfig = null,
    double MaxAgeHours = 26,
    string? CanaryPath = null);

/// <summary>
/// File-level backup discovery. Every adapter yields BackupTier.FileBackup artifacts
/// with TargetService = the source's configured name, so one check covers them all.
/// </summary>
public sealed class FileBackupProvider(ISshProvider ssh)
{
    public async Task<IReadOnlyList<BackupArtifact>> GetAsync(FileBackupSource source, CancellationToken ct = default)
    {
        switch (source.Kind)
        {
            case "restic":
            {
                var json = await RunAsync(source.Alias,
                    $"restic -r '{source.Repo}' --password-file '{source.PasswordFile}' snapshots --json --no-lock", ct);
                return ParseResticSnapshots(json, source.Name);
            }
            case "borg":
            {
                // BORG_PASSCOMMAND keeps the passphrase on the host, like restic's
                // --password-file — it never crosses the wire.
                var json = await RunAsync(source.Alias,
                    $"BORG_PASSCOMMAND='cat {source.PasswordFile}' borg list --json '{source.Repo}'", ct);
                return ParseBorgArchives(json, source.Name);
            }
            case "dir":
            {
                var listing = await RunAsync(source.Alias,
                    $"find '{source.Path}' -maxdepth 1 -type f -printf '%f\\t%s\\t%T@\\n'", ct);
                return ParseDirListing(listing, source.Name, $"{source.Alias}:{source.Path}");
            }
            case "kopia":
            {
                // Kopia is connection-stateful: the host's root user connected the repo
                // once (password stays in kopia's own config); listing needs no secret.
                var json = await RunAsync(source.Alias, "kopia snapshot list --json --all", ct);
                return ParseKopiaSnapshots(json, source.Name);
            }
            case "snapper":
            {
                var json = await RunAsync(source.Alias,
                    $"snapper --jsonout -c '{source.SnapperConfig}' list", ct);
                return ParseSnapperSnapshots(json, source.Name);
            }
            case "haos":
            {
                var envelope = await RunAsync(source.Alias,
                    $"qm guest exec {source.Vmid} --timeout 60 -- ha backups --raw-json", ct);
                return ParseHaBackups(envelope, source.Name);
            }
            default:
                throw new ProviderException($"Unknown fileBackups kind '{source.Kind}' for '{source.Name}'.");
        }
    }

    public static IReadOnlyList<BackupArtifact> ParseResticSnapshots(string json, string sourceName)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(el => new BackupArtifact(
            Tier: BackupTier.FileBackup,
            TargetService: sourceName,
            Location: $"restic {el.GetProperty("short_id").GetString()} [{string.Join(", ", el.GetProperty("paths").EnumerateArray().Select(p => p.GetString()))}]",
            Timestamp: el.GetProperty("time").GetDateTimeOffset(),
            SizeBytes: -1, // restic doesn't report per-snapshot size in this listing
            Method: "restic",
            HasOffsiteCopy: false,
            Status: "ok")).ToList();
    }

    public static IReadOnlyList<BackupArtifact> ParseBorgArchives(string json, string sourceName)
    {
        using var doc = JsonDocument.Parse(json);
        var artifacts = new List<BackupArtifact>();

        foreach (var a in doc.RootElement.GetProperty("archives").EnumerateArray())
        {
            // Borg emits naive local timestamps (no offset); treated as UTC — the
            // ≤ a-few-hours skew is well inside the freshness margins.
            if (!DateTimeOffset.TryParse(a.GetProperty("time").GetString(),
                    CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var time))
            {
                continue;
            }

            artifacts.Add(new BackupArtifact(
                Tier: BackupTier.FileBackup,
                TargetService: sourceName,
                Location: $"borg archive {a.GetProperty("name").GetString()}",
                Timestamp: time,
                SizeBytes: -1, // sizes need `borg info` per archive; unknown ≠ empty
                Method: "borg",
                HasOffsiteCopy: false,
                Status: "ok"));
        }
        return artifacts;
    }

    public static IReadOnlyList<BackupArtifact> ParseDirListing(string listing, string sourceName, string locationPrefix)
    {
        var artifacts = new List<BackupArtifact>();
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length != 3
                || !long.TryParse(parts[1], out var size)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch))
            {
                continue;
            }

            artifacts.Add(new BackupArtifact(
                Tier: BackupTier.FileBackup,
                TargetService: sourceName,
                Location: $"{locationPrefix}/{parts[0]}",
                Timestamp: DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000)),
                SizeBytes: size,
                Method: "archive",
                HasOffsiteCopy: false,
                Status: "ok"));
        }
        return artifacts;
    }

    public static IReadOnlyList<BackupArtifact> ParseKopiaSnapshots(string json, string sourceName)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(s => new BackupArtifact(
            Tier: BackupTier.FileBackup,
            TargetService: sourceName,
            Location: $"kopia {s.GetProperty("source").GetProperty("path").GetString()} @ {s.GetProperty("id").GetString()?[..8]}",
            Timestamp: s.GetProperty("startTime").GetDateTimeOffset(),
            SizeBytes: s.TryGetProperty("stats", out var st) && st.TryGetProperty("totalSize", out var ts)
                ? ts.GetInt64() : -1,
            Method: "kopia",
            HasOffsiteCopy: false,
            Status: "ok")).ToList();
    }

    /// <summary>snapper --jsonout list: one root property (the config name) holding
    /// the snapshot array. Entry number 0 is the synthetic "current" state (empty
    /// date) and is skipped — it isn't a snapshot.</summary>
    public static IReadOnlyList<BackupArtifact> ParseSnapperSnapshots(string json, string sourceName)
    {
        using var doc = JsonDocument.Parse(json);
        var artifacts = new List<BackupArtifact>();

        foreach (var config in doc.RootElement.EnumerateObject())
        {
            foreach (var s in config.Value.EnumerateArray())
            {
                var date = s.TryGetProperty("date", out var d) ? d.GetString() : null;
                if (string.IsNullOrEmpty(date))
                    continue; // number 0 / "current"
                if (!DateTimeOffset.TryParseExact(date, "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var time))
                {
                    continue;
                }

                artifacts.Add(new BackupArtifact(
                    Tier: BackupTier.FileBackup,
                    TargetService: sourceName,
                    Location: $"snapper #{s.GetProperty("number").GetInt32()} ({s.GetProperty("description").GetString()})",
                    Timestamp: time,
                    SizeBytes: -1,
                    Method: "snapper",
                    HasOffsiteCopy: false,
                    Status: "ok"));
            }
        }
        return artifacts;
    }

    /// <summary>Unwraps the qemu guest-exec envelope, then keeps only FULL system
    /// backups — HA's automatic pre-update add-on backups are partial and do not
    /// make the system restorable.</summary>
    public static IReadOnlyList<BackupArtifact> ParseHaBackups(string envelopeJson, string sourceName)
    {
        using var envelope = JsonDocument.Parse(envelopeJson);
        var outData = envelope.RootElement.GetProperty("out-data").GetString() ?? "{}";

        using var doc = JsonDocument.Parse(outData);
        var backups = doc.RootElement.GetProperty("data").GetProperty("backups");

        var artifacts = new List<BackupArtifact>();
        foreach (var b in backups.EnumerateArray())
        {
            var isFull = b.TryGetProperty("type", out var t) && t.GetString() == "full";
            if (!isFull && b.TryGetProperty("content", out var c)
                        && c.TryGetProperty("homeassistant", out var ha) && ha.GetBoolean())
            {
                isFull = true; // partial that still includes the HA core config
            }
            if (!isFull)
                continue;

            artifacts.Add(new BackupArtifact(
                Tier: BackupTier.FileBackup,
                TargetService: sourceName,
                Location: $"ha backup {b.GetProperty("slug").GetString()} '{b.GetProperty("name").GetString()}'",
                Timestamp: DateTimeOffset.Parse(b.GetProperty("date").GetString()!, CultureInfo.InvariantCulture),
                SizeBytes: b.TryGetProperty("size_bytes", out var s) ? s.GetInt64() : 0,
                Method: "ha-full",
                HasOffsiteCopy: false,
                Status: "ok"));
        }
        return artifacts;
    }

    /// <summary>
    /// Restore canary: stream the configured sentinel file out of the LATEST snapshot
    /// and count the bytes ON THE HOST (`| wc -c`) — the content never crosses the
    /// wire and nothing is written anywhere, so the probe stays read-only. The
    /// pipeline's exit code is wc's, so a failed restore surfaces as 0 bytes plus the
    /// tool's stderr, which the check turns into a finding (not a provider error).
    /// </summary>
    public async Task<CanaryResult> ProbeCanaryAsync(FileBackupSource source, CancellationToken ct = default)
    {
        var canary = source.CanaryPath
            ?? throw new ProviderException($"fileBackups '{source.Name}' has no canaryPath.");

        switch (source.Kind)
        {
            case "restic":
            {
                var result = await ssh.RunAsync(source.Alias,
                    $"restic -r '{source.Repo}' --password-file '{source.PasswordFile}' --no-lock dump latest '{canary}' | wc -c", ct);
                return ToCanaryResult(source, canary, result);
            }
            case "borg":
            {
                // Borg needs the newest archive's NAME (no 'latest' selector), and
                // stores paths without the leading slash.
                var listing = await RunAsync(source.Alias,
                    $"BORG_PASSCOMMAND='cat {source.PasswordFile}' borg list --json --last 1 '{source.Repo}'", ct);
                var archive = ParseBorgLatestArchive(listing)
                    ?? throw new ProviderException($"borg repo '{source.Repo}' has no archives to probe ({source.Name}).");
                var result = await ssh.RunAsync(source.Alias,
                    $"BORG_PASSCOMMAND='cat {source.PasswordFile}' borg extract --stdout '{source.Repo}::{archive}' '{canary.TrimStart('/')}' | wc -c", ct);
                return ToCanaryResult(source, canary, result);
            }
            default:
                throw new ProviderException($"canaryPath is not supported for fileBackups kind '{source.Kind}' ({source.Name}).");
        }
    }

    public static string? ParseBorgLatestArchive(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("archives").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .LastOrDefault(n => !string.IsNullOrEmpty(n));
    }

    private static CanaryResult ToCanaryResult(FileBackupSource source, string canary, SshResult result)
    {
        // Non-zero here is wc/ssh-level breakage (unreachable host, no wc), not a
        // failed restore — that one exits 0 with a 0 count and stderr from the tool.
        if (result.ExitCode != 0)
            throw new ProviderException($"canary probe on {source.Alias} failed: {result.StdErr.Trim()}");
        if (!long.TryParse(result.StdOut.Trim(), out var bytes))
            throw new ProviderException($"canary probe on {source.Alias}: unexpected byte count '{result.StdOut.Trim()}'.");

        var detail = result.StdErr.Trim();
        return new CanaryResult(source.Name, source.Alias, canary, bytes, detail.Length > 0 ? detail : null);
    }

    private async Task<string> RunAsync(string alias, string command, CancellationToken ct)
    {
        var result = await ssh.RunAsync(alias, command, ct);
        if (result.ExitCode != 0)
            throw new ProviderException($"'{command}' on {alias} failed: {result.StdErr.Trim()}");
        return result.StdOut;
    }
}
