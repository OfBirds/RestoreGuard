using RestoreGuard.Cli;
using RestoreGuard.Providers.Docker;
using RestoreGuard.Providers.Pve;

namespace RestoreGuard.Tests;

public class DoctorTests
{
    private static RestoreGuardConfig FullConfig() => new(
        DockerHosts: [new DockerHostConfig("lab98"), new DockerHostConfig("lab55", "/usr/bin/docker")],
        LogicalDbBackups: [new LogicalDbBackupConfig("lab55", "/var/backups/db-prod", ["lab55"])],
        PveNodes: [new PveNodeConfig("lab99", "pve", ["pbs-nvme", "nas_backup"]), new PveNodeConfig("lab142", "host1")],
        PbsMaxSnapshotAgeHours: 26,
        TrueNas: new TrueNasCliConfig("truenas", []),
        PbsOffsite: new PbsOffsiteCliConfig("lab99", "/var/log/pbs-onedrive-sync.log", "onedrive:", "pbs-nvme"),
        PbsMaintenance: new PbsMaintenanceCliConfig("lab99", 110, "main", "pve"),
        SmartHosts: ["lab99", "lab142"],
        FileBackups:
        [
            new("restic", "restic", "lab98", Repo: "/misc/repo", PasswordFile: "/root/.restic-pass"),
            new("borg", "borg", "lab55", Repo: "/var/backups/borg", PasswordFile: "/root/.borg-pass"),
            new("tarballs", "dir", "lab118", Path: "/opt/volume-backups"),
            new("ha", "haos", "lab99", Vmid: 9000),
            new("snapper", "snapper", "lab55", SnapperConfig: "rgdata"),
            new("kopia", "kopia", "lab118"),
        ],
        SuppressionsFile: null);

    [Fact]
    public void EveryConfiguredSurfaceGetsAProbe()
    {
        var probes = Doctor.BuildProbes(FullConfig());

        // 2 docker + 1 dumps + 2 pve + 2 storage-content + 1 truenas + 1 offsite
        // + 1 maintenance + 2 smart + 6 file-backup
        Assert.Equal(18, probes.Count);
        Assert.Equal(
            ["db-dumps", "docker", "file-backup", "pbs-maintenance", "pbs-offsite", "pve", "smart", "truenas"],
            probes.Select(p => p.Area).Distinct().Order(StringComparer.Ordinal).ToList());

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
    public void EmptyConfigYieldsNoProbes()
    {
        var config = new RestoreGuardConfig([], null, null, 0, null, null, null, null, null, null);
        Assert.Empty(Doctor.BuildProbes(config));
    }
}
