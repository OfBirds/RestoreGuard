using RestoreGuard.Core.Model;
using RestoreGuard.Providers;
using RestoreGuard.Providers.Kubernetes;

namespace RestoreGuard.Tests;

public class KubernetesProviderTests
{
    [Fact]
    public void ParsesClusterSurfacesAndKeepsVeleroSizeUnknown()
    {
        var nodes = KubectlParser.ParseNodes(Fixtures.Read("k8s-nodes.json"));
        Assert.Equal(["Ready", "NotReady"], nodes.Select(n => n.State));
        Assert.Equal(2, KubectlParser.ParseDeployments(Fixtures.Read("k8s-deployments.json")).Count);
        Assert.Equal("Pending", KubectlParser.ParsePersistentVolumeClaims(Fixtures.Read("k8s-pvcs.json"))[1].Phase);
        var backup = Assert.Single(KubectlParser.ParseBackups(Fixtures.Read("k8s-velero.json"), "cluster-a"), b => b.Status == "Completed");
        Assert.Equal(BackupTier.KubernetesBackup, backup.Tier);
        Assert.Equal(0, backup.SizeBytes);
        var podVolumeBackups = KubectlParser.ParsePodVolumeBackups(Fixtures.Read("k8s-podvolumebackups.json"), "velero");
        Assert.Equal(2, podVolumeBackups.Count);
        Assert.Contains(podVolumeBackups, backup => backup.BackupName == "nightly-20260825" && backup.Completed);
        Assert.Contains(podVolumeBackups, backup => backup.BackupName == "failed-20260824" && !backup.Completed);
        Assert.Contains(KubectlParser.ParsePodVolumeClaims(Fixtures.Read("k8s-pods.json")),
            reference => reference.Namespace == "apps" && reference.PodName == "web-0" && reference.Volume == "data" && reference.ClaimName == "data");
        Assert.Contains(KubectlParser.ParseVolumeSnapshots(Fixtures.Read("k8s-volumesnapshots.json")),
            snapshot => snapshot.BackupName == "nightly-20260825" && snapshot.Namespace == "apps" && snapshot.ClaimName == "data" && snapshot.ReadyToUse);
    }

    [Fact]
    public void DeploymentWithoutSpecReplicasDefaultsToOne()
    {
        var deployments = KubectlParser.ParseDeployments("""
            { "items": [{ "metadata": { "namespace": "apps", "name": "web" }, "spec": {}, "status": {} }] }
            """);

        Assert.Equal(1, Assert.Single(deployments).DesiredReplicas);
    }

    [Fact]
    public async Task NullVeleroAndDisabledWorkloadsMakeNoSurfaceCalls()
    {
        var ssh = new RecordingSsh();
        var result = await new KubernetesProvider(ssh).GetClusterAsync(new("cluster-b", "target", VeleroNamespace: null, CheckWorkloads: false));
        Assert.Empty(result.Services);
        Assert.Empty(ssh.Commands);
    }

    [Fact]
    public async Task MissingVeleroResourceIsCoverageStateButForbiddenAndMalformedResponsesAreProviderErrors()
    {
        var missing = new ScriptedSsh(command => command.Contains("get pvc -A")
            ? new SshResult(0, "{\"items\":[]}", "")
            : new SshResult(1, "", Fixtures.Read("k8s-no-velero.stderr")));
        var missingResult = await new KubernetesProvider(missing).GetAsync(new("cluster-a", "target", CheckWorkloads: false));
        Assert.False(missingResult.State.VeleroInstalled);
        Assert.Equal(2, missing.Commands.Count);

        var forbidden = new ScriptedSsh(command => command.Contains("get pvc -A")
            ? new SshResult(0, "{\"items\":[]}", "")
            : new SshResult(1, "", "Error from server (Forbidden): backups.velero.io is forbidden"));
        var forbiddenError = await Assert.ThrowsAsync<KubernetesProviderException>(() =>
            new KubernetesProvider(forbidden).GetAsync(new("cluster-a", "target", CheckWorkloads: false)));
        Assert.Equal(KubernetesProviderErrorKind.Forbidden, forbiddenError.Kind);

        var malformed = new ScriptedSsh(command => command.Contains("get pvc -A")
            ? new SshResult(0, "{\"items\":[]}", "")
            : new SshResult(0, "not-json", ""));
        var malformedError = await Assert.ThrowsAsync<KubernetesProviderException>(() =>
            new KubernetesProvider(malformed).GetAsync(new("cluster-a", "target", CheckWorkloads: false)));
        Assert.Equal(KubernetesProviderErrorKind.Malformed, malformedError.Kind);

        var wrongShape = new ScriptedSsh(command => command.Contains("get pvc -A")
            ? new SshResult(0, "{\"items\":[]}", "")
            : new SshResult(0, "{\"items\":{}}", ""));
        var wrongShapeError = await Assert.ThrowsAsync<KubernetesProviderException>(() =>
            new KubernetesProvider(wrongShape).GetAsync(new("cluster-a", "target", CheckWorkloads: false)));
        Assert.Equal(KubernetesProviderErrorKind.Malformed, wrongShapeError.Kind);
    }

    [Fact]
    public async Task VeleroQueriesIncludeSchedulesAndPodVolumeBackups()
    {
        var ssh = new ScriptedSsh(command => command switch
        {
            var value when value.Contains("get pvc -A") => new SshResult(0, Fixtures.Read("k8s-pvcs.json"), ""),
            var value when value.Contains("podvolumebackups.velero.io") => new SshResult(0, Fixtures.Read("k8s-podvolumebackups.json"), ""),
            var value when value.Contains("get pods -A") => new SshResult(0, Fixtures.Read("k8s-pods.json"), ""),
            var value when value.Contains("volumesnapshots.snapshot.storage.k8s.io") => new SshResult(0, Fixtures.Read("k8s-volumesnapshots.json"), ""),
            var value when value.Contains("backups.velero.io") => new SshResult(0, Fixtures.Read("k8s-velero.json"), ""),
            var value when value.Contains("schedules.velero.io") => new SshResult(0, Fixtures.Read("k8s-schedules.json"), ""),
            _ => throw new InvalidOperationException($"Unexpected command: {command}"),
        });

        var ticker = new List<string>();
        var result = await new KubernetesProvider(ssh, ticker.Add).GetAsync(new("cluster-a", "target", CheckWorkloads: false));

        Assert.True(result.State.VeleroInstalled);
        Assert.Equal(2, result.State.PodVolumeBackups.Count);
        Assert.Single(result.State.VolumeSnapshots);
        Assert.Equal("data", result.State.PodVolumeBackups.Single(backup => backup.BackupName == "nightly-20260825").PersistentVolumeClaim);
        Assert.Contains(ssh.Commands, command => command.Contains("schedules.velero.io"));
        Assert.Contains(ssh.Commands, command => command.Contains("podvolumebackups.velero.io"));
        Assert.Contains(ssh.Commands, command => command.Contains("get pods -A"));
        Assert.Contains(ssh.Commands, command => command.Contains("volumesnapshots.snapshot.storage.k8s.io"));
        Assert.Equal(["persistent-volume-claims", "velero-backups", "velero-schedules", "pod-volume-backups", "pods", "csi-volume-snapshots"],
            ticker.Select(message => message[(message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]).ToList());
    }

    [Fact]
    public async Task MissingOptionalCsiSnapshotResourceDoesNotHideCompletedPodVolumeBackup()
    {
        var ssh = new ScriptedSsh(command => command switch
        {
            var value when value.Contains("get pvc -A") => new SshResult(0, Fixtures.Read("k8s-pvcs.json"), ""),
            var value when value.Contains("podvolumebackups.velero.io") => new SshResult(0, Fixtures.Read("k8s-podvolumebackups.json"), ""),
            var value when value.Contains("backups.velero.io") => new SshResult(0, Fixtures.Read("k8s-velero.json"), ""),
            var value when value.Contains("schedules.velero.io") => new SshResult(0, Fixtures.Read("k8s-schedules.json"), ""),
            var value when value.Contains("get pods -A") => new SshResult(0, Fixtures.Read("k8s-pods.json"), ""),
            var value when value.Contains("volumesnapshots.snapshot.storage.k8s.io") => new SshResult(1, "", "the server doesn't have a resource type \"volumesnapshots\""),
            _ => throw new InvalidOperationException($"Unexpected command: {command}"),
        });

        var result = await new KubernetesProvider(ssh).GetAsync(new("cluster-a", "target", CheckWorkloads: false));

        Assert.Empty(result.State.VolumeSnapshots);
        Assert.Contains(result.State.PodVolumeBackups, backup => backup.BackupName == "nightly-20260825" && backup.PersistentVolumeClaim == "data");
    }

    private sealed class RecordingSsh : ISshProvider
    {
        public List<string> Commands { get; } = [];
        public Task<SshResult> RunAsync(string alias, string command, CancellationToken ct = default)
        { Commands.Add(command); return Task.FromResult(new SshResult(0, "{\"items\":[]}", "")); }
    }

    private sealed class ScriptedSsh(Func<string, SshResult> respond) : ISshProvider
    {
        public List<string> Commands { get; } = [];
        public Task<SshResult> RunAsync(string alias, string command, CancellationToken ct = default)
        {
            Commands.Add(command);
            return Task.FromResult(respond(command));
        }
    }
}
