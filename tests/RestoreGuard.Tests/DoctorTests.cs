using RestoreGuard.Cli;
using RestoreGuard.Providers;
using RestoreGuard.Providers.Docker;
using RestoreGuard.Providers.Pve;
using RestoreGuard.Providers.Kubernetes;
using RestoreGuard.Providers.S3;

namespace RestoreGuard.Tests;

[Collection("reports-env")]
public class DoctorTests
{
    private static RestoreGuardConfig FullConfig() => new(
        DockerHosts: [new DockerHostConfig("lab98"), new DockerHostConfig("lab55", "/usr/bin/docker")],
        LogicalDbBackups: [new LogicalDbBackupConfig("lab55", "/var/backups/db-prod", ["lab55"])],
        PveNodes: [new PveNodeConfig("lab99", "pve", ["pbs-nvme", "nas_backup"]), new PveNodeConfig("lab142", "host1")],
        PbsMaxSnapshotAgeHours: 26,
        TrueNas: new TrueNasCliConfig("truenas", []),
        PbsOffsite: new PbsOffsiteCliConfig("lab99", "/var/log/pbs-onedrive-sync.log", "onedrive:", "pbs-nvme"),
        PbsMaintenance: new PbsMaintenanceCliConfig("lab99", 110, "main", "pve", HostBackups: ["lab98"]),
        SmartHosts: ["lab99", "lab142"],
        FileBackups:
        [
            new("restic", "restic", "lab98", Repo: "/misc/repo", PasswordFile: "/root/.restic-pass", CanaryPath: "/opt/x/docker-compose.yml"),
            new("borg", "borg", "lab55", Repo: "/var/backups/borg", PasswordFile: "/root/.borg-pass", CanaryPath: "/etc/fstab"),
            new("tarballs", "dir", "lab118", Path: "/opt/volume-backups"),
            new("ha", "haos", "lab99", Vmid: 9000),
            new("snapper", "snapper", "lab55", SnapperConfig: "rgdata"),
            new("kopia", "kopia", "lab118"),
        ],
        SuppressionsFile: null,
        ZfsReplications:
        [
            new("pve data", "lab99", "tank/data", "lab142", "backup/pve-data"),
            new("scratch", "lab99", "tank/scratch"),
        ],
        OffsiteJobs:
        [
            new("onedrive push", "lab99", "/var/log/pbs-onedrive-sync.log", "onedrive:"),
            new("usb copy", "lab55", "/var/log/usb-sync.log"),
        ],
        SqliteBackupDirs: [new("appdata copies", "lab98", "/backups/appdata")]);

    [Fact]
    public void EveryConfiguredSurfaceGetsAProbe()
    {
        var probes = Doctor.BuildProbes(FullConfig());

        // 2 docker + 1 dumps + 2 pve + 2 storage-content + 1 truenas + 1 legacy-offsite
        // + 2 maintenance (version + datastore list for host backups) + 2 smart
        // + 6 file-backup + 2 restore-canary + 3 zfs (source+target, source-only)
        // + 2 offsite jobs + 1 sqlite dir
        Assert.Equal(27, probes.Count);
        Assert.Equal(
            ["db-dumps", "docker", "file-backup", "offsite", "pbs-maintenance", "pbs-offsite", "pve", "restore-canary", "smart", "sqlite", "truenas", "zfs-replication"],
            probes.Select(p => p.Area).Distinct().Order(StringComparer.Ordinal).ToList());
        // The sqlite scan dir gets an existence preflight.
        Assert.Single(probes, p => p.Area == "sqlite" && p.Command == "[ -d '/backups/appdata' ]");

        // The per-host docker path quirk carries into the probe command.
        Assert.Contains(probes, p => p.Host == "lab55" && p.Command.StartsWith("/usr/bin/docker", StringComparison.Ordinal));
        // Every listed backup storage gets its own content probe.
        Assert.Single(probes, p => p.Command.Contains("storage/pbs-nvme/content"));
        Assert.Single(probes, p => p.Command.Contains("storage/nas_backup/content"));
        // Each file-backup kind gets its adapter-specific probe.
        Assert.Contains(probes, p => p.Command.StartsWith("restic", StringComparison.Ordinal));
        Assert.Contains(probes, p => p.Command.Contains("BORG_PASSCOMMAND"));
        Assert.Contains(probes, p => p.Command.Contains("qm guest cmd 9000 ping"));
    }

    [Fact]
    public void OffsiteJobs_ProbeLogAndOptionallyTheRemote()
    {
        var probes = Doctor.BuildProbes(FullConfig()).Where(p => p.Area == "offsite").ToList();

        Assert.Equal(2, probes.Count);
        var withRemote = Assert.Single(probes, p => p.Host == "lab99");
        Assert.Equal("test -r '/var/log/pbs-onedrive-sync.log' && rclone about onedrive: --json > /dev/null", withRemote.Command);
        var logOnly = Assert.Single(probes, p => p.Host == "lab55");
        Assert.Equal("test -r '/var/log/usb-sync.log'", logOnly.Command);
        Assert.Equal("sync log readable (usb copy)", logOnly.Requirement);
    }

    [Fact]
    public void ZfsEntries_ProbeSourceAndReplicaDatasetsExist()
    {
        var probes = Doctor.BuildProbes(FullConfig()).Where(p => p.Area == "zfs-replication").ToList();

        Assert.Equal(3, probes.Count);
        Assert.Contains(probes, p => p.Host == "lab99" && p.Command == "zfs list 'tank/data' > /dev/null");
        Assert.Contains(probes, p => p.Host == "lab142" && p.Command.Contains("'backup/pve-data'")
            && p.Requirement == "replica dataset 'backup/pve-data' exists (pve data)");
        // A snapshot-only entry probes only its source.
        Assert.Single(probes, p => p.Requirement.Contains("(scratch)"));
    }

    [Fact]
    public void CanarySources_GetARealRestoreProbe()
    {
        var probes = Doctor.BuildProbes(FullConfig()).Where(p => p.Area == "restore-canary").ToList();

        Assert.Equal(2, probes.Count);
        // Restic dumps 'latest' directly; borg resolves the newest archive name first
        // and uses the stored path (leading slash stripped) — the same restores the
        // audit runs, byte-counted on the host.
        var restic = Assert.Single(probes, p => p.Host == "lab98");
        Assert.Contains("dump latest '/opt/x/docker-compose.yml' | wc -c", restic.Command);
        Assert.Contains("-gt 0", restic.Command);
        var borg = Assert.Single(probes, p => p.Host == "lab55");
        Assert.Contains("borg list --short --last 1", borg.Command);
        Assert.Contains("'etc/fstab'", borg.Command);
        Assert.Contains("canary '/etc/fstab' restores from the latest snapshot (borg)", borg.Requirement);
    }

    [Fact]
    public void EmptyConfigYieldsNoProbes()
    {
        var config = new RestoreGuardConfig([], null, null, 0, null, null, null, null, null, null);
        Assert.Empty(Doctor.BuildProbes(config));
    }

    [Fact]
    public void KubernetesDoctor_ProbesEveryEnabledQueryAndRequiresYes()
    {
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null,
            KubernetesClusters: [new KubernetesClusterConfig("cluster-a", "pve99")]);
        var probes = Doctor.BuildProbes(config).Where(p => p.Area == "k8s").ToList();

        Assert.Equal(9, probes.Count);
        Assert.Contains(probes, p => p.Command == "kubectl get --raw=/readyz > /dev/null");
        Assert.All(probes.Where(p => p.Command.Contains("auth can-i") && !p.Command.Contains("volumesnapshots.snapshot.storage.k8s.io")), p => Assert.Equal("yes", p.ExpectedStdOut));
        Assert.Contains(probes, p => p.Command.Contains("list backups.velero.io -n 'velero'"));
        Assert.Contains(probes, p => p.Command.Contains("list schedules.velero.io -n 'velero'"));
        Assert.Contains(probes, p => p.Command.Contains("list podvolumebackups.velero.io -n 'velero'"));
        Assert.Contains(probes, p => p.Command.Contains("list pods --all-namespaces"));
        Assert.Contains(probes, p => p.Command.Contains("api-resources --api-group=snapshot.storage.k8s.io")
            && p.Command.Contains("list volumesnapshots.snapshot.storage.k8s.io --all-namespaces")
            && p.ExpectedStdOut is null);
        Assert.Contains(probes, p => p.Command.Contains("list deployments.apps --all-namespaces"));
        Assert.Contains(probes, p => p.Command.Contains("list persistentvolumeclaims --all-namespaces"));
    }

    [Fact]
    public void KubernetesDoctor_DisabledVeleroAndWorkloadsHasNoAssociatedQueries()
    {
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null,
            KubernetesClusters: [new KubernetesClusterConfig("cluster-a", "pve99", VeleroNamespace: null, CheckWorkloads: false)]);
        var probes = Doctor.BuildProbes(config).Where(p => p.Area == "k8s").ToList();

        Assert.Single(probes);
        Assert.DoesNotContain(probes, p => p.Command.Contains("auth can-i"));
    }

    [Fact]
    public void KubernetesDoctor_VeleroOnlyStillPreflightsPvcCoverage()
    {
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null,
            KubernetesClusters: [new KubernetesClusterConfig("cluster-a", "pve99", CheckWorkloads: false)]);
        var probes = Doctor.BuildProbes(config).Where(probe => probe.Area == "k8s").ToList();

        Assert.Equal(7, probes.Count);
        Assert.Contains(probes, probe => probe.Command.Contains("list persistentvolumeclaims --all-namespaces"));
        Assert.DoesNotContain(probes, probe => probe.Command.Contains("list nodes"));
        Assert.DoesNotContain(probes, probe => probe.Command.Contains("list deployments.apps"));
    }

    [Fact]
    public async Task KubernetesDoctor_RejectsCanIOutputOtherThanYes()
    {
        var reportDir = Directory.CreateTempSubdirectory("rg-doctor-test");
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null,
            Reporting: new ReportingConfig(new FolderSinkConfig(reportDir.FullName)),
            KubernetesClusters: [new KubernetesClusterConfig("cluster-a", "pve99")]);
        var output = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            var exit = await Doctor.RunAsync(config, new DoctorSsh(), reportDir.FullName);
            Assert.Equal(2, exit);
        }
        finally
        {
            Console.SetOut(original);
            reportDir.Delete(recursive: true);
        }

        Assert.Contains("expected stdout 'yes', got 'no'", output.ToString());
    }

    [Fact]
    public async Task S3Doctor_PreflightsSignedReadOnlyMetadata()
    {
        var reportDir = Directory.CreateTempSubdirectory("rg-s3-doctor");
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null,
            ObjectStorageBuckets: [new S3BucketConfig("offsite", "https://s3.example.com", "bucket")]);
        var output = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            var exit = await Doctor.RunAsync(
                config, new DoctorSsh(), reportDir.FullName, new FakeStorage(lockEnabled: false));
            Assert.Equal(2, exit);
        }
        finally
        {
            Console.SetOut(original);
            reportDir.Delete(recursive: true);
        }

        Assert.Contains("Object Lock is not enabled", output.ToString());
    }

    private sealed class FakeStorage(bool lockEnabled) : IObjectStorageProvider
    {
        public Task<S3BucketAudit> GetBucketAsync(
            S3BucketConfig config, string configDir, CancellationToken ct = default) => Task.FromResult(
            new S3BucketAudit(
                config.Name,
                config.Bucket,
                new("Enabled", false),
                lockEnabled ? new(true, "COMPLIANCE", 30, null) : new(false, null, null, null),
                null,
                false));
    }

    private sealed class DoctorSsh : ISshProvider
    {
        public Task<SshResult> RunAsync(string alias, string command, CancellationToken ct = default) =>
            Task.FromResult(command.Contains("auth can-i", StringComparison.Ordinal)
                ? new SshResult(0, "no\n", "")
                : new SshResult(0, "", ""));
    }
}
