using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers;
using RestoreGuard.Providers.Pve;

namespace RestoreGuard.Tests;

/// <summary>Serves PBS fixtures keyed by the proxmox-backup-manager subcommand.</summary>
file sealed class FakePbsSsh : ISshProvider
{
    public List<string> Commands { get; } = [];

    public Task<SshResult> RunAsync(string hostAlias, string command, CancellationToken ct = default)
    {
        Commands.Add(command);
        var stdout = command switch
        {
            _ when command.Contains("garbage-collection status") => Fixtures.Read("pbs-gc-status.json"),
            _ when command.Contains("verify-job list") => Fixtures.Read("pbs-verify-jobs.json"),
            _ when command.Contains("sync-job list") => Fixtures.Read("pbs-sync-job-list.json"),
            _ when command.Contains("task list") => Fixtures.Read("pbs-task-list-sync.json"),
            _ when command.Contains("datastore list") => Fixtures.Read("pbs-datastore-list.json"),
            _ when command.Contains("ls -1") => Fixtures.Read("pbs-host-snapshots.txt"),
            _ => throw new InvalidOperationException($"Unexpected command: {command}"),
        };
        return Task.FromResult(new SshResult(0, stdout, ""));
    }
}

public class PbsSyncAndHostBackupTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<Finding> Run(DatastoreMaintenance info, PbsMaintenanceOptions? options = null) =>
        new PbsMaintenanceCheck(info, options ?? new PbsMaintenanceOptions("pve", TimeSpan.FromDays(7)))
            .Evaluate(LabInventory.Empty with { CapturedAt = Now }).ToList();

    private static DatastoreMaintenance Fresh() => new(
        "main", Now.AddDays(-1), VerifyJobCount: 1, VerifyLastRun: Now.AddHours(-10), VerifyLastStatus: "OK");

    // ---------- parsers ----------

    [Fact]
    public void SyncJobList_AndSyncTasks_Parse()
    {
        Assert.Equal(1, PbsMaintenanceProvider.ParseJobCount(Fixtures.Read("pbs-sync-job-list.json")));

        var (time, status) = PbsMaintenanceProvider.ParseLastTask(Fixtures.Read("pbs-task-list-sync.json"), "sync");
        // The newest sync is the OK one — the older timed-out run must not win.
        Assert.Equal("OK", status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1783216800), time);
    }

    [Fact]
    public void DatastorePath_FoundByName_NullForUnknown()
    {
        var json = Fixtures.Read("pbs-datastore-list.json");
        Assert.Equal("/mnt/datastore/main", PbsMaintenanceProvider.ParseDatastorePath(json, "main"));
        Assert.Null(PbsMaintenanceProvider.ParseDatastorePath(json, "nope"));
    }

    [Fact]
    public void HostSnapshots_NewestRfc3339DirWins_StrayFilesIgnored()
    {
        var newest = PbsMaintenanceProvider.ParseNewestHostSnapshot(Fixtures.Read("pbs-host-snapshots.txt"));

        Assert.Equal(new DateTimeOffset(2026, 7, 5, 1, 0, 3, TimeSpan.Zero), newest);
        Assert.Null(PbsMaintenanceProvider.ParseNewestHostSnapshot(""));
    }

    // ---------- provider ----------

    [Fact]
    public async Task GetAsync_DiscoversSyncJobsAndHostBackups()
    {
        var ssh = new FakePbsSsh();

        var info = await new PbsMaintenanceProvider(ssh).GetAsync(
            new PbsMaintenanceConfig("lab99", 110, "main", HostBackups: ["lab98"]));

        Assert.Equal(1, info.SyncJobCount);
        Assert.Equal("OK", info.SyncLastStatus);
        var hb = Assert.Single(info.HostBackups!);
        Assert.Equal("lab98", hb.Id);
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 1, 0, 3, TimeSpan.Zero), hb.Newest);
        // The host listing goes through the datastore's on-disk path, inside the CT.
        Assert.Contains(ssh.Commands, c => c.Contains("ls -1 '/mnt/datastore/main/host/lab98'"));
    }

    // ---------- check: sync jobs ----------

    [Fact]
    public void SyncJobOutcomes_NeverRan_Failed_Stale()
    {
        Assert.Equal("pbs/sync-job-never-ran",
            Assert.Single(Run(Fresh() with { SyncJobCount = 1 })).RuleId);

        var failed = Assert.Single(Run(Fresh() with
        {
            SyncJobCount = 1, SyncLastRun = Now.AddHours(-2), SyncLastStatus = "connection error: timed out",
        }));
        Assert.Equal(("pbs/sync-job-failed", Severity.Red), (failed.RuleId, failed.Severity));

        var stale = Assert.Single(Run(Fresh() with
        {
            SyncJobCount = 1, SyncLastRun = Now.AddDays(-4), SyncLastStatus = "OK",
        }));
        Assert.Equal(("pbs/sync-job-stale", Severity.Red), (stale.RuleId, stale.Severity));
    }

    [Fact]
    public void NoSyncJobsOnThePbs_IsQuiet()
    {
        Assert.Empty(Run(Fresh()));
    }

    // ---------- check: host backups ----------

    [Fact]
    public void HostBackupOutcomes_Missing_Stale_FreshQuiet()
    {
        var findings = Run(Fresh() with
        {
            HostBackups =
            [
                new PbsHostBackup("never-backed-up", null),
                new PbsHostBackup("stale-host", Now.AddDays(-3)),
                new PbsHostBackup("good-host", Now.AddHours(-11)),
            ],
        });

        Assert.Equal(2, findings.Count);
        Assert.Equal("pbs/host-backup-missing", Assert.Single(findings, f => f.Service == "never-backed-up").RuleId);
        Assert.Equal("pbs/host-backup-stale", Assert.Single(findings, f => f.Service == "stale-host").RuleId);
        Assert.All(findings, f => Assert.Equal(Severity.Red, f.Severity));
    }
}
