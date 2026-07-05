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
    string? SuppressionsFile)
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
        var errors = config.Validate();
        if (errors.Count == 0)
            return config;

        Console.Error.WriteLine($"Config {path} has problems:");
        foreach (var e in errors)
            Console.Error.WriteLine($"  - {e}");
        Console.Error.WriteLine("Fix the file by hand, or run `restoreguard init` to redo guided setup.");
        return null;
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
        }

        if (PbsOffsite is { } off && string.IsNullOrWhiteSpace(off.Alias))
            errors.Add("pbsOffsite.alias is empty.");
        if (PbsMaintenance is { } pm && string.IsNullOrWhiteSpace(pm.ExecAlias))
            errors.Add("pbsMaintenance.execAlias is empty.");

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
    double MaxVerifyAgeHours = 50);
