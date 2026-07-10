using System.Diagnostics;
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

        // Every probe registers here so the ticker below can name what is
        // still running — a silent parallel audit is indistinguishable from a
        // frozen one, and that ambiguity is itself a bug.
        var probes = new List<(string Kind, string Host, Task Task)>();
        Task<(string, T?, string?)> Track<T>(string kind, string host, Task<T> task) where T : class
        {
            var wrapped = WithHost(kind, host, task);
            probes.Add((kind, host, wrapped));
            return wrapped;
        }

        var dockerTasks = config.DockerHosts
            .Select(h => Track("docker", h.Alias, docker.GetServicesAsync(h)))
            .ToList();
        var pveTasks = (config.PveNodes ?? [])
            .Select(n => Track("pve", n.Alias, pve.GetNodeAsync(n)))
            .ToList();
        var dumpTasks = (config.LogicalDbBackups ?? [])
            .Select(db => Track("db-dumps", db.Host, dbDump.GetArtifactsAsync(db.Host, db.Path, db.Method)))
            .ToList();
        var trueNasTask = config.TrueNas is { } tn
            ? Track("truenas", tn.Alias, new TrueNasProvider(ssh).GetAsync(new TrueNasConfig(tn.Alias, tn.ExcludeDatasets)))
            : Task.FromResult<(string, TrueNasProvider.TrueNasInventory?, string?)>(("", null, null));
        var offsiteProvider = new PbsOffsiteProvider(ssh);
        var offsiteTask = config.PbsOffsite is { } off
            ? Track("offsite", off.Alias, offsiteProvider.GetAsync(new PbsOffsiteConfig(off.Alias, off.LogPath, off.RcloneRemote, off.TargetName)))
            : Task.FromResult<(string, PbsOffsiteProvider.OffsiteState?, string?)>(("", null, null));
        var offsiteJobTasks = (config.OffsiteJobs ?? [])
            .Select(o => Track("offsite", $"{o.Alias} ({o.Name})", offsiteProvider.GetJobAsync(o)))
            .ToList();
        var maintenanceTask = config.PbsMaintenance is { } pm
            ? Track("pbs-maint", pm.ExecAlias, new PbsMaintenanceProvider(ssh).GetAsync(new PbsMaintenanceConfig(pm.ExecAlias, pm.ContainerId, pm.Datastore, pm.HostBackups)))
            : Task.FromResult<(string, DatastoreMaintenance?, string?)>(("", null, null));
        var smartProvider = new SmartProvider(ssh);
        var smartTasks = (config.SmartHosts ?? [])
            .Select(h => Track("smart", h, smartProvider.GetAsync(h)))
            .ToList();
        var fileBackupProvider = new FileBackupProvider(ssh);
        var fileBackupTasks = (config.FileBackups ?? [])
            .Select(s => Track("files", $"{s.Alias} ({s.Name})", fileBackupProvider.GetAsync(s)))
            .ToList();
        var canaryTasks = (config.FileBackups ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.CanaryPath))
            .Select(s => Track("canary", $"{s.Alias} ({s.Name})", fileBackupProvider.ProbeCanaryAsync(s)))
            .ToList();
        var zfsProvider = new Providers.Zfs.ZfsProvider(ssh);
        var zfsTasks = (config.ZfsReplications ?? [])
            .Select(z => Track("zfs", $"{z.SourceAlias} ({z.Name})", zfsProvider.GetAsync(z)))
            .ToList();
        var sqliteProvider = new Providers.Sqlite.SqliteBackupProvider(ssh);
        var sqliteTasks = (config.SqliteBackupDirs ?? [])
            .Select(s => Track("sqlite", $"{s.Alias} ({s.Name})", sqliteProvider.GetAsync(s)))
            .ToList();

        Progress($"auditing: {probes.Count} probe(s) across the lab, in parallel (Ctrl+C stops and reports what finished)...");

        var discovery = Stopwatch.StartNew();
        var all = Task.WhenAll(probes.Select(p => p.Task));
        while (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10))) != all)
        {
            var pending = probes.Where(p => !p.Task.IsCompleted)
                .Select(p => $"[{p.Kind}] {p.Host}").ToList();
            if (pending.Count > 0)
                Progress($"  ... still waiting ({discovery.Elapsed.TotalSeconds:0}s): {string.Join(", ", pending)}");
        }
        Progress($"discovery done in {discovery.Elapsed.TotalSeconds:0.0}s - evaluating checks...");

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

        foreach (var (jobHost, jobState, jobError) in offsiteJobTasks.Select(t => t.Result))
        {
            if (jobState is not null)
            {
                if (jobState.LastSync is not null) artifacts.Add(jobState.LastSync);
                if (jobState.Remote is not null) storage.Add(jobState.Remote);
            }
            if (jobError is not null) providerErrors.Add($"{jobHost}: {jobError}");
        }

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

        var canaries = new List<CanaryResult>();
        foreach (var (host, canary, error) in canaryTasks.Select(t => t.Result))
        {
            if (canary is not null) canaries.Add(canary);
            if (error is not null) providerErrors.Add($"{host}: {error}");
        }

        var zfsStates = new List<ZfsReplicationState>();
        foreach (var (host, state, error) in zfsTasks.Select(t => t.Result))
        {
            if (state is not null) zfsStates.Add(state);
            if (error is not null) providerErrors.Add($"{host}: {error}");
        }

        var sqliteScans = new List<SqliteHotCopyScan>();
        foreach (var (host, scan, error) in sqliteTasks.Select(t => t.Result))
        {
            if (scan is not null) sqliteScans.Add(scan);
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
            checks.Add(new ThreeTwoOneCheck(pveStorages
                .Select(s => new StorageLocality(s.Target.Name, s.Target.Host, s.Shared))
                .ToList()));
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
        if (config.OffsiteJobs is { Count: > 0 } jobs)
        {
            checks.Add(new OffsiteJobCheck(jobs
                .Select(o => new OffsiteJobExpectation(o.Name, o.Alias, TimeSpan.FromHours(o.MaxSyncAgeHours)))
                .ToList()));
        }
        if (maintenance is not null && config.PbsMaintenance is { } pmc)
        {
            checks.Add(new PbsMaintenanceCheck(maintenance, new PbsMaintenanceOptions(
                pmc.Host, TimeSpan.FromDays(pmc.MaxGcAgeDays), TimeSpan.FromHours(pmc.MaxVerifyAgeHours),
                TimeSpan.FromHours(pmc.MaxSyncJobAgeHours), TimeSpan.FromHours(pmc.MaxHostBackupAgeHours))));
        }
        if (config.FileBackups is { Count: > 0 } fbs)
        {
            checks.Add(new FileBackupCheck(fbs
                .Select(s => new FileBackupExpectation(s.Name, s.Alias, TimeSpan.FromHours(s.MaxAgeHours)))
                .ToList()));
        }
        if (canaries.Count > 0)
        {
            checks.Add(new RestoreCanaryCheck(canaries));
        }
        if (sqliteScans.Count > 0)
        {
            checks.Add(new SqliteHotCopyCheck(sqliteScans));
        }
        if (zfsStates.Count > 0 && config.ZfsReplications is { } zrs)
        {
            checks.Add(new ZfsReplicationCheck(zfsStates, zrs
                .Select(z => new ZfsReplicationExpectation(z.Name,
                    TimeSpan.FromHours(z.MaxSnapshotAgeHours), TimeSpan.FromHours(z.MaxReplicaAgeHours)))
                .ToList()));
        }

        var report = new CheckEngine(checks).Run(inventory, suppressions, DateTimeOffset.UtcNow);

        // Build the sinks up front so their connection ids can be stamped into the
        // report's own metadata before it is serialized and delivered.
        var sinks = ReportPublisher.BuildSinks(config, configDir);
        var reportJson = JsonReportWriter.Write(report, inventory, providerErrors,
            sinks.Select(s => s.Id).ToList());

        if (jsonOutput)
        {
            Console.WriteLine(reportJson);
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

        // Persist AFTER the report is on stdout: a dead sink can't eat the report,
        // it can only (loudly) fail the exit code.
        var sinkFailures = await ReportPublisher.PublishAsync(
            sinks, reportJson, report.GeneratedAt, Progress);

        return report.Overall == Severity.Red || providerErrors.Count > 0 || sinkFailures > 0 ? 1 : 0;
    }

    // A provider that dies must degrade to a visible error, never kill the whole audit.
    private static async Task<(string Host, T? Result, string? Error)> WithHost<T>(string kind, string host, Task<T> task) where T : class
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await task;
            Progress($"  ok    [{kind}] {host} ({sw.Elapsed.TotalSeconds:0.0}s)");
            return (host, result, null);
        }
        catch (Exception ex)
        {
            Progress($"  FAIL  [{kind}] {host}: {ex.Message} ({sw.Elapsed.TotalSeconds:0.0}s)");
            return (host, null, ex.Message);
        }
    }

    /// <summary>Progress goes to stderr: visible live in a terminal and in cron
    /// logs, while stdout stays exactly the report (the --json contract).</summary>
    private static void Progress(string line) => Console.Error.WriteLine(line);
}
