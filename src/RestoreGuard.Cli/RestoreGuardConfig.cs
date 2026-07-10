using System.Text.Json;
using RestoreGuard.Core;
using RestoreGuard.Providers.Docker;
using RestoreGuard.Providers.Pve;

namespace RestoreGuard.Cli;

public sealed record RestoreGuardConfig(
    IReadOnlyList<DockerHostConfig> DockerHosts,
    IReadOnlyList<LogicalDbBackupConfig>? LogicalDbBackups,
    IReadOnlyList<PveNodeConfig>? PveNodes,
    double PbsMaxSnapshotAgeHours,
    TrueNasCliConfig? TrueNas,
    PbsOffsiteCliConfig? PbsOffsite,
    PbsMaintenanceCliConfig? PbsMaintenance,
    IReadOnlyList<string>? SmartHosts,
    IReadOnlyList<RestoreGuard.Providers.FileBackups.FileBackupSource>? FileBackups,
    string? SuppressionsFile,
    IReadOnlyList<RestoreGuard.Providers.Zfs.ZfsReplicationConfig>? ZfsReplications = null,
    IReadOnlyList<RestoreGuard.Providers.Offsite.OffsiteJobConfig>? OffsiteJobs = null,
    IReadOnlyList<RestoreGuard.Providers.Sqlite.SqliteBackupDirConfig>? SqliteBackupDirs = null,
    // Report destinations live in their OWN self-contained file (default the wizard
    // writes: reporting.json) so the SAME file can be handed to another tool — e.g.
    // HCC reads it to connect to the exact same folder/bucket/DB and pull the reports
    // RestoreGuard wrote there. Path relative to this config. The inline `Reporting`
    // section still works but is the legacy form; the wizard writes the file.
    string? ReportingFile = null,
    ReportingConfig? Reporting = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static RestoreGuardConfig Load(string path) =>
        JsonSerializer.Deserialize<RestoreGuardConfig>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException($"Config {path} deserialized to null.");

    /// <summary>Loads and validates; prints errors + the fix hint and returns null on problems.</summary>
    public static RestoreGuardConfig? LoadValidated(string path)
    {
        var config = Load(path);
        var errors = config.Validate().ToList();
        // Validate the separate reporting file too (it isn't reachable from the
        // parameterless Validate(), which has no directory to resolve it against).
        config.LoadReporting(Path.GetDirectoryName(Path.GetFullPath(path))!, errors);
        if (errors.Count == 0)
            return config;

        Console.Error.WriteLine($"Config {path} has problems:");
        foreach (var e in errors)
            Console.Error.WriteLine($"  - {e}");
        Console.Error.WriteLine("Fix the file by hand, or run `restoreguard init` to redo guided setup.");
        return null;
    }

    /// <summary>
    /// The report destinations, resolved from either <c>reportingFile</c> (the
    /// preferred, self-contained form — the same file HCC reads to pull reports back)
    /// or the inline <c>reporting</c> section. <see cref="ResolvedReporting.SecretsBaseDir"/>
    /// is the directory the sink's <c>*File</c> secrets resolve against — for a
    /// separate file that is the FILE's own directory, so the file is portable to HCC;
    /// for the inline form it is the config's directory. Adds any problems to
    /// <paramref name="errors"/>.
    /// </summary>
    public ResolvedReporting LoadReporting(string configDir, List<string> errors)
    {
        if (ReportingFile is null)
            return new ResolvedReporting(Reporting, configDir); // inline: Validate() already checked it

        if (Reporting is not null)
            errors.Add("Set reportingFile OR an inline reporting section, not both.");

        var path = Path.IsPathRooted(ReportingFile) ? ReportingFile : Path.Combine(configDir, ReportingFile);
        if (!File.Exists(path))
        {
            errors.Add($"reportingFile not found: {path}");
            return new ResolvedReporting(null, configDir);
        }

        ReportingConfig? reporting;
        try
        {
            reporting = JsonSerializer.Deserialize<ReportingConfig>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            errors.Add($"reportingFile {path} is not valid JSON: {ex.Message}");
            return new ResolvedReporting(null, configDir);
        }
        reporting?.Validate(errors);
        return new ResolvedReporting(reporting, Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    /// <summary>Catches configs that would produce garbage SSH errors mid-run —
    /// above all empty host/path fields (a half-answered wizard, a hand-edit typo).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        foreach (var (h, i) in DockerHosts.Select((h, i) => (h, i)))
        {
            if (string.IsNullOrWhiteSpace(h.Alias))
                errors.Add($"dockerHosts[{i}].alias is empty.");
        }

        foreach (var (db, i) in (LogicalDbBackups ?? []).Select((db, i) => (db, i)))
        {
            if (string.IsNullOrWhiteSpace(db.Host)) errors.Add($"logicalDbBackups[{i}].host is empty.");
            if (string.IsNullOrWhiteSpace(db.Path)) errors.Add($"logicalDbBackups[{i}].path is empty.");
            if (db.Method is not ("pg_dumpall" or "mysqldump" or "mongodump"))
                errors.Add($"logicalDbBackups[{i}].method '{db.Method}' is unknown (pg_dumpall, mysqldump, mongodump).");
        }

        foreach (var (n, i) in (PveNodes ?? []).Select((n, i) => (n, i)))
        {
            if (string.IsNullOrWhiteSpace(n.Alias)) errors.Add($"pveNodes[{i}].alias is empty.");
            if (string.IsNullOrWhiteSpace(n.Node)) errors.Add($"pveNodes[{i}].node is empty.");
        }

        if (TrueNas is { } tn && string.IsNullOrWhiteSpace(tn.Alias))
            errors.Add("trueNas.alias is empty.");

        foreach (var (h, i) in (SmartHosts ?? []).Select((h, i) => (h, i)))
        {
            if (string.IsNullOrWhiteSpace(h))
                errors.Add($"smartHosts[{i}] is empty.");
        }

        foreach (var (s, i) in (FileBackups ?? []).Select((s, i) => (s, i)))
        {
            if (string.IsNullOrWhiteSpace(s.Name)) errors.Add($"fileBackups[{i}].name is empty.");
            if (string.IsNullOrWhiteSpace(s.Alias)) errors.Add($"fileBackups[{i}].alias is empty.");
            var kindError = s.Kind switch
            {
                "restic" or "borg" when string.IsNullOrWhiteSpace(s.Repo) || string.IsNullOrWhiteSpace(s.PasswordFile) =>
                    $"fileBackups[{i}] ({s.Kind}) needs repo and passwordFile.",
                "dir" when string.IsNullOrWhiteSpace(s.Path) => $"fileBackups[{i}] (dir) needs path.",
                "haos" when s.Vmid is null => $"fileBackups[{i}] (haos) needs vmid.",
                "snapper" when string.IsNullOrWhiteSpace(s.SnapperConfig) => $"fileBackups[{i}] (snapper) needs snapperConfig.",
                "restic" or "borg" or "dir" or "haos" or "snapper" or "kopia" => null,
                _ => $"fileBackups[{i}].kind '{s.Kind}' is unknown (restic, borg, kopia, dir, haos, snapper).",
            };
            if (kindError is not null) errors.Add(kindError);
            if (!string.IsNullOrWhiteSpace(s.CanaryPath) && s.Kind is not ("restic" or "borg"))
                errors.Add($"fileBackups[{i}].canaryPath is only supported for restic and borg (kind is '{s.Kind}').");
        }

        foreach (var (z, i) in (ZfsReplications ?? []).Select((z, i) => (z, i)))
        {
            if (string.IsNullOrWhiteSpace(z.Name)) errors.Add($"zfsReplications[{i}].name is empty.");
            if (string.IsNullOrWhiteSpace(z.SourceAlias)) errors.Add($"zfsReplications[{i}].sourceAlias is empty.");
            if (string.IsNullOrWhiteSpace(z.SourceDataset)) errors.Add($"zfsReplications[{i}].sourceDataset is empty.");
            // Target comes as a pair or not at all — half a target silently checks nothing.
            if (string.IsNullOrWhiteSpace(z.TargetAlias) != string.IsNullOrWhiteSpace(z.TargetDataset))
                errors.Add($"zfsReplications[{i}] needs BOTH targetAlias and targetDataset (or neither for snapshot-only).");
        }

        foreach (var (o, i) in (OffsiteJobs ?? []).Select((o, i) => (o, i)))
        {
            if (string.IsNullOrWhiteSpace(o.Name)) errors.Add($"offsiteJobs[{i}].name is empty.");
            if (string.IsNullOrWhiteSpace(o.Alias)) errors.Add($"offsiteJobs[{i}].alias is empty.");
            if (string.IsNullOrWhiteSpace(o.LogPath)) errors.Add($"offsiteJobs[{i}].logPath is empty.");
        }

        foreach (var (s, i) in (SqliteBackupDirs ?? []).Select((s, i) => (s, i)))
        {
            if (string.IsNullOrWhiteSpace(s.Name)) errors.Add($"sqliteBackupDirs[{i}].name is empty.");
            if (string.IsNullOrWhiteSpace(s.Alias)) errors.Add($"sqliteBackupDirs[{i}].alias is empty.");
            if (string.IsNullOrWhiteSpace(s.Path)) errors.Add($"sqliteBackupDirs[{i}].path is empty.");
        }

        if (PbsOffsite is { } off && string.IsNullOrWhiteSpace(off.Alias))
            errors.Add("pbsOffsite.alias is empty.");
        if (PbsMaintenance is { } pm && string.IsNullOrWhiteSpace(pm.ExecAlias))
            errors.Add("pbsMaintenance.execAlias is empty.");

        Reporting?.Validate(errors);

        return errors;
    }

    public IReadOnlyList<Suppression> LoadSuppressions(string configDir)
    {
        if (SuppressionsFile is null)
            return [];
        var path = Path.IsPathRooted(SuppressionsFile)
            ? SuppressionsFile
            : Path.Combine(configDir, SuppressionsFile);
        return JsonSerializer.Deserialize<List<Suppression>>(File.ReadAllText(path), JsonOptions) ?? [];
    }
}

public sealed record LogicalDbBackupConfig(
    string Host,
    string Path,
    IReadOnlyList<string> CoversHosts,
    double MaxDumpAgeHours = 26,
    string Method = "pg_dumpall",
    bool RequireProdNaming = true);

public sealed record TrueNasCliConfig(
    string Alias,
    IReadOnlyList<string> ExcludeDatasets,
    double MaxSnapshotAgeHours = 26,
    double MaxSyncAgeHours = 26);

public sealed record PbsOffsiteCliConfig(
    string Alias,
    string LogPath,
    string RcloneRemote,
    string TargetName,
    double MaxSyncAgeHours = 26);

public sealed record PbsMaintenanceCliConfig(
    string ExecAlias,
    int ContainerId,
    string Datastore,
    string Host,
    double MaxGcAgeDays = 7,
    double MaxVerifyAgeHours = 50,
    double MaxSyncJobAgeHours = 26,
    // proxmox-backup-client backup ids (usually hostnames) expected under host/
    // in the datastore — bare-metal hosts backed up straight to PBS.
    IReadOnlyList<string>? HostBackups = null,
    double MaxHostBackupAgeHours = 26);
