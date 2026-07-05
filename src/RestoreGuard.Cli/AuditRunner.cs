using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers;
using RestoreGuard.Providers.DbDump;
using RestoreGuard.Providers.Docker;
using RestoreGuard.Providers.FileBackups;
using RestoreGuard.Providers.Offsite;
using RestoreGuard.Providers.Pve;
using RestoreGuard.Providers.Smart;
using RestoreGuard.Providers.TrueNas;

namespace RestoreGuard.Cli;

/// <summary>Runs one full audit: all configured providers in parallel, checks, report.</summary>
public static class AuditRunner
{
    public static async Task<int> RunAsync(RestoreGuardConfig config, string configDir, ISshProvider ssh, bool jsonOutput)
    {
        var suppressions = config.LoadSuppressions(configDir);

        var docker = new DockerProvider(ssh);
        var dbDump = new DbDumpProvider(ssh);
        var pve = new PveProvider(ssh);

        var dockerTasks = config.DockerHosts
            .Select(h => WithHost(h.Alias, docker.GetServicesAsync(h)))
            .ToList();
        var pveTasks = (config.PveNodes ?? [])
            .Select(n => WithHost(n.Alias, pve.GetNodeAsync(n)))
            .ToList();
        var dumpTasks = (config.LogicalDbBackups ?? [])
            .Select(db => WithHost(db.Host, dbDump.GetArtifactsAsync(db.Host, db.Path, db.Method)))
            .ToList();
        var trueNasTask = config.TrueNas is { } tn
            ? WithHost(tn.Alias, new TrueNasProvider(ssh).GetAsync(new TrueNasConfig(tn.Alias, tn.ExcludeDatasets)))
            : Task.FromResult<(string, TrueNasProvider.TrueNasInventory?, string?)>(("", null, null));
        var offsiteTask = config.PbsOffsite is { } off
            ? WithHost(off.Alias, new PbsOffsiteProvider(ssh).GetAsync(new PbsOffsiteConfig(off.Alias, off.LogPath, off.RcloneRemote, off.TargetName)))
            : Task.FromResult<(string, PbsOffsiteProvider.OffsiteState?, string?)>(("", null, null));
        var maintenanceTask = config.PbsMaintenance is { } pm
            ? WithHost(pm.ExecAlias, new PbsMaintenanceProvider(ssh).GetAsync(new PbsMaintenanceConfig(pm.ExecAlias, pm.ContainerId, pm.Datastore)))
            : Task.FromResult<(string, DatastoreMaintenance?, string?)>(("", null, null));
        var smartProvider = new SmartProvider(ssh);
        var smartTasks = (config.SmartHosts ?? [])
            .Select(h => WithHost(h, smartProvider.GetAsync(h)))
            .ToList();
        var fileBackupProvider = new FileBackupProvider(ssh);
        var fileBackupTasks = (config.FileBackups ?? [])
            .Select(s => WithHost($"{s.Alias} ({s.Name})", fileBackupProvider.GetAsync(s)))
            .ToList();

        await Task.WhenAll([.. dockerTasks, .. pveTasks, .. dumpTasks, trueNasTask, offsiteTask, maintenanceTask, .. smartTasks, .. fileBackupTasks]);

        var services = new List<Service>();
        var artifacts = new List<BackupArtifact>();
        var providerErrors = new List<string>();

        foreach (var (host, result, error) in dockerTasks.Select(t => t.Result))
        {
            if (result is not null) services.AddRange(result);
            if (error is not null) providerErrors.Add($"{host}: {error}");
        }

        // PBS snapshots join against guests from ALL nodes (shared datastore, colliding
        // vmids), so collect every node inventory before assembling artifacts.
        var guests = new List<PveGuest>();
        var snapshots = new List<PbsSnapshot>();
        var pveStorages = new List<PveStorage>();
        foreach (var (host, node, error) in pveTasks.Select(t => t.Result))
        {
            if (node is not null)
            {
                guests.AddRange(node.Guests);
                snapshots.AddRange(node.PbsSnapshots);
                pveStorages.AddRange(node.Storages);
            }
            if (error is not null) providerErrors.Add($"{host}: {error}");
        }
        services.AddRange(guests.Select(g => g.ToService()));
        artifacts.AddRange(PveArtifactAssembler.Join(guests, snapshots));

        foreach (var (dumpHost, dumps, dumpError) in dumpTasks.Select(t => t.Result))
        {
            if (dumps is not null) artifacts.AddRange(dumps);
            if (dumpError is not null) providerErrors.Add($"{dumpHost}: {dumpError}");
        }

        var storage = new List<StorageTarget>(PveProvider.MergeStorages(pveStorages));
        var (tnHost, tnInventory, tnError) = trueNasTask.Result;
        if (tnInventory is not null)
        {
            storage.AddRange(tnInventory.Storage);
            artifacts.AddRange(tnInventory.Artifacts);
        }
        if (tnError is not null) providerErrors.Add($"{tnHost}: {tnError}");

        var (offHost, offsite, offError) = offsiteTask.Result;
        if (offsite is not null)
        {
            if (offsite.LastSync is not null) artifacts.Add(offsite.LastSync);
            storage.Add(offsite.Remote);
        }
        if (offError is not null) providerErrors.Add($"{offHost}: {offError}");

        var (pmHost, maintenance, pmError) = maintenanceTask.Result;
        if (pmError is not null) providerErrors.Add($"{pmHost}: {pmError}");

        foreach (var (host, disks, error) in smartTasks.Select(t => t.Result))
        {
            if (disks is not null) storage.AddRange(disks);
            if (error is not null) providerErrors.Add($"{host}: {error}");
        }

        foreach (var (host, fileArtifacts, error) in fileBackupTasks.Select(t => t.Result))
        {
            if (fileArtifacts is not null) artifacts.AddRange(fileArtifacts);
            if (error is not null) providerErrors.Add($"{host}: {error}");
        }

        var inventory = new LabInventory(DateTimeOffset.UtcNow, services, artifacts, storage);

        List<ICheck> checks = [new MountDriftCheck(), new ConfigDriftCheck(), new StorageCapacityCheck(new StorageCapacityOptions())];
        foreach (var dbc in config.LogicalDbBackups ?? [])
        {
            checks.Add(new DbBackupCoverageCheck(new DbBackupCoverageOptions(
                dbc.CoversHosts, TimeSpan.FromHours(dbc.MaxDumpAgeHours), dbc.Method, dbc.RequireProdNaming)));
        }
        if (config.PveNodes is { Count: > 0 })
        {
            checks.Add(new ImageBackupCheck(new ImageBackupOptions(
                TimeSpan.FromHours(config.PbsMaxSnapshotAgeHours > 0 ? config.PbsMaxSnapshotAgeHours : 26))));
        }
        if (config.TrueNas is { } tnc)
        {
            checks.Add(new TrueNasBackupCheck(new TrueNasBackupOptions(
                tnc.Alias,
                TimeSpan.FromHours(tnc.MaxSnapshotAgeHours),
                TimeSpan.FromHours(tnc.MaxSyncAgeHours))));
        }
        if (config.PbsOffsite is { } offc)
        {
            checks.Add(new PbsOffsiteCheck(new PbsOffsiteOptions(
                offc.Alias, TimeSpan.FromHours(offc.MaxSyncAgeHours))));
        }
        if (maintenance is not null && config.PbsMaintenance is { } pmc)
        {
            checks.Add(new PbsMaintenanceCheck(maintenance, new PbsMaintenanceOptions(
                pmc.Host, TimeSpan.FromDays(pmc.MaxGcAgeDays), TimeSpan.FromHours(pmc.MaxVerifyAgeHours))));
        }
        if (config.FileBackups is { Count: > 0 } fbs)
        {
            checks.Add(new FileBackupCheck(fbs
                .Select(s => new FileBackupExpectation(s.Name, s.Alias, TimeSpan.FromHours(s.MaxAgeHours)))
                .ToList()));
        }

        var report = new CheckEngine(checks).Run(inventory, suppressions, DateTimeOffset.UtcNow);

        if (jsonOutput)
        {
            Console.WriteLine(JsonReportWriter.Write(report, inventory, providerErrors));
        }
        else
        {
            ReportRenderer.Render(report, inventory);

            if (providerErrors.Count > 0)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Provider errors (discovery incomplete — treat this run as partial):");
                Console.ResetColor();
                foreach (var e in providerErrors)
                    Console.WriteLine($"  - {e}");
            }
        }

        return report.Overall == Severity.Red || providerErrors.Count > 0 ? 1 : 0;
    }

    // A provider that dies must degrade to a visible error, never kill the whole audit.
    private static async Task<(string Host, T? Result, string? Error)> WithHost<T>(string host, Task<T> task) where T : class
    {
        try
        {
            return (host, await task, null);
        }
        catch (Exception ex)
        {
            return (host, null, ex.Message);
        }
    }
}
