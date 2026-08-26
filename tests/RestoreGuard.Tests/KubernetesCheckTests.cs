using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Kubernetes;

namespace RestoreGuard.Tests;

public class KubernetesCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static LabInventory Inventory(IEnumerable<BackupArtifact>? backups = null, IEnumerable<Service>? services = null) =>
        new(Now, services == null ? [] : services.ToList(), backups == null ? [] : backups.ToList(), []);

    private static KubernetesState State(bool installed = true,
        IReadOnlyList<KubernetesPersistentVolumeClaim>? pvcs = null,
        IReadOnlyList<KubernetesSchedule>? schedules = null,
        IReadOnlyList<KubernetesPodVolumeBackup>? podVolumeBackups = null,
        IReadOnlyList<KubernetesVolumeSnapshot>? volumeSnapshots = null) =>
        new("cluster-a", pvcs ?? [], schedules ?? [], installed, podVolumeBackups ?? [], volumeSnapshots ?? [], Now);

    private static BackupArtifact Backup(string name, string phase, DateTimeOffset timestamp) =>
        new(BackupTier.KubernetesBackup, "cluster-a velero", name, timestamp, 0, "velero", false, phase);

    private static List<Finding> Findings(KubernetesState state, LabInventory inventory, bool velero = true, bool workloads = true) =>
        new KubernetesCheck([state], [new KubernetesExpectation("cluster-a", "pve99", TimeSpan.FromHours(26), workloads, velero)])
            .Evaluate(inventory).ToList();

    [Fact]
    public void MissingProviderStateIsUnreachableAndDoesNotInventFindings()
    {
        var findings = new KubernetesCheck([], [new KubernetesExpectation("cluster-a", "pve99", TimeSpan.FromHours(26))])
            .Evaluate(Inventory()).ToList();
        Assert.Contains(findings, finding => finding.RuleId == "k8s/unreachable" && finding.Service == "cluster-a cluster" && finding.Host == "cluster-a");
    }

    [Fact]
    public void MissingVeleroAndZeroBackupsAreDistinguished()
    {
        Assert.Contains(Findings(State(installed: false), Inventory()), f => f.RuleId == "k8s-backup/velero-missing");
        Assert.Contains(Findings(State(), Inventory(), velero: true), f => f.RuleId == "k8s-backup/no-backups");
    }

    [Fact]
    public void InProgressOnlyDoesNotPretendToBeCompletedOrStale()
    {
        var inventory = Inventory([Backup("running", "InProgress", Now.AddHours(-1))]);
        var findings = Findings(State(schedules: [new KubernetesSchedule("velero", "nightly")]), inventory);
        Assert.DoesNotContain(findings, f => f.RuleId == "k8s-backup/stale");
        Assert.DoesNotContain(findings, f => f.RuleId == "k8s-backup/failed");
        Assert.Contains(findings, f => f.RuleId == "k8s-backup/no-completed");
    }

    [Fact]
    public void CompletedStaleAndFailedPhasesHaveStableIdentities()
    {
        var inventory = Inventory([
            Backup("stale", "Completed", Now.AddHours(-30)),
            Backup("failed", "PartiallyFailed", Now.AddHours(-2))]);
        var findings = Findings(State(schedules: [new KubernetesSchedule("velero", "nightly")]), inventory);

        Assert.Contains(findings, f => (f.RuleId, f.Service, f.Host) == ("k8s-backup/stale", "cluster-a cluster", "cluster-a"));
        Assert.Contains(findings, f => (f.RuleId, f.Service, f.Host) == ("k8s-backup/failed", "cluster-a cluster", "cluster-a"));
    }

    [Fact]
    public void NewerCompletedBackupClearsAnOlderFailure()
    {
        var inventory = Inventory([
            Backup("failed", "Failed", Now.AddHours(-2)),
            Backup("recovered", "Completed", Now.AddHours(-1))]);
        var findings = Findings(State(schedules: [new KubernetesSchedule("velero", "nightly")]), inventory);

        Assert.DoesNotContain(findings, finding => finding.RuleId == "k8s-backup/failed");
    }

    [Fact]
    public void NewerCompletedBackupClearsAnOlderPodVolumeBackupFailure()
    {
        var inventory = Inventory([
            Backup("snapshot-failed", "Completed", Now.AddHours(-2)),
            Backup("recovered", "Completed", Now.AddHours(-1))]);
        var state = State(
            pvcs: [new KubernetesPersistentVolumeClaim("apps", "data", "Bound")],
            schedules: [new KubernetesSchedule("velero", "nightly")],
            podVolumeBackups:
            [
                new KubernetesPodVolumeBackup("snapshot-failed", "apps", "web-0", "data", "Failed", "data"),
                new KubernetesPodVolumeBackup("recovered", "apps", "web-0", "data", "Completed", "data"),
            ]);

        Assert.DoesNotContain(Findings(state, inventory), finding => finding.RuleId == "k8s-pvc/unprotected");
    }

    [Fact]
    public void MissingScheduleIsYellowAndMissingPodVolumeBackupIsRed()
    {
        var inventory = Inventory([Backup("ok", "Completed", Now.AddHours(-1))]);
        var findings = Findings(State(
            pvcs: [new KubernetesPersistentVolumeClaim("apps", "data", "Bound")],
            podVolumeBackups: []), inventory);
        Assert.Contains(findings, f => f.RuleId == "k8s-backup/no-schedule" && f.Severity == Severity.Yellow);
        Assert.Contains(findings, f => f.RuleId == "k8s-pvc/unprotected" && f.Service == "apps/data");
    }

    [Fact]
    public void CompletedPodVolumeBackupProtectsBoundPvcWhenSnapshotCountersAreAbsent()
    {
        var inventory = Inventory([Backup("ok", "Completed", Now.AddHours(-1))]);
        var state = State(
            pvcs: [new KubernetesPersistentVolumeClaim("apps", "data", "Bound")],
            schedules: [new KubernetesSchedule("velero", "nightly")],
            podVolumeBackups: [new KubernetesPodVolumeBackup("ok", "apps", "web-0", "data", "Completed", "data")]);

        Assert.DoesNotContain(Findings(state, inventory), finding => finding.RuleId == "k8s-pvc/unprotected");
    }

    [Fact]
    public void ReadyCsiSnapshotProtectsBoundPvcWhenPodVolumeBackupIsAbsent()
    {
        var inventory = Inventory([Backup("ok", "Completed", Now.AddHours(-1))]);
        var state = State(
            pvcs: [new KubernetesPersistentVolumeClaim("apps", "data", "Bound")],
            schedules: [new KubernetesSchedule("velero", "nightly")],
            volumeSnapshots: [new KubernetesVolumeSnapshot("ok", "apps", "data", true)]);

        Assert.DoesNotContain(Findings(state, inventory), finding => finding.RuleId == "k8s-pvc/unprotected");
    }

    [Fact]
    public void EachBoundPvcNeedsItsOwnCompletedPodVolumeBackup()
    {
        var inventory = Inventory([Backup("ok", "Completed", Now.AddHours(-1))]);
        var state = State(
            pvcs:
            [
                new KubernetesPersistentVolumeClaim("apps", "data", "Bound"),
                new KubernetesPersistentVolumeClaim("apps", "uploads", "Bound"),
            ],
            podVolumeBackups: [new KubernetesPodVolumeBackup("ok", "apps", "web-0", "data", "Completed", "data")]);

        var unprotected = Findings(state, inventory).Where(finding => finding.RuleId == "k8s-pvc/unprotected").ToList();
        Assert.Single(unprotected);
        Assert.Equal("apps/uploads", unprotected[0].Service);
    }

    [Fact]
    public void WorkloadChecksCoverPvcDeploymentAndNodeButCanBeDisabled()
    {
        var state = State(pvcs: [new("apps", "data", "Pending")]);
        var services = new[]
        {
            new Service("apps/web", "cluster-a", ServiceKind.K8sWorkload, "degraded", null, [], null),
            new Service("node-1", "cluster-a", ServiceKind.K8sNode, "NotReady", null, [], null),
        };
        var findings = Findings(state, Inventory(services: services));
        Assert.Contains(findings, f => f.RuleId == "k8s-pvc/unbound" && f.Service == "apps/data");
        Assert.Contains(findings, f => f.RuleId == "k8s-workload/unavailable" && f.Service == "apps/web");
        Assert.Contains(findings, f => f.RuleId == "k8s-node/not-ready" && f.Service == "node-1");
        Assert.DoesNotContain(Findings(state, Inventory(services: services), velero: false, workloads: false),
            finding => finding.RuleId.StartsWith("k8s-", StringComparison.Ordinal));
    }

    [Fact]
    public void BackupFindingIdentityCanBeSuppressedWithoutADeadTarget()
    {
        var inventory = Inventory(services: [new Service("cluster-a cluster", "cluster-a", ServiceKind.K8sCluster, "unknown", null, [], null)]);
        var check = new KubernetesCheck([State(installed: false)],
            [new KubernetesExpectation("cluster-a", "pve99", TimeSpan.FromHours(26))]);
        var suppression = new Suppression("cluster-a", "cluster-a cluster", "k8s-backup/velero-missing", "cluster is intentionally disposable", new(2026, 8, 25));
        var report = new CheckEngine([check]).Run(inventory, [suppression], Now);

        Assert.DoesNotContain(report.Findings, finding => finding.RuleId == "k8s-backup/velero-missing");
        Assert.Contains(report.SuppressedFindings, f => f.RuleId == "k8s-backup/velero-missing" && f.Service == "cluster-a cluster" && f.Host == "cluster-a");
        Assert.DoesNotContain(report.Findings, f => f.RuleId == "suppression/unknown-target");
    }
}
