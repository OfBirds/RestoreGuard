using RestoreGuard.Cli;
using RestoreGuard.Providers.Kubernetes;

namespace RestoreGuard.Tests;

public class KubernetesConfigTests
{
    private static RestoreGuardConfig Config(params KubernetesClusterConfig[] clusters) =>
        new([], null, null, 26, null, null, null, null, null, null,
            KubernetesClusters: clusters);

    [Fact]
    public void ValidClusterDefaultsVeleroAndHasNoValidationErrors()
    {
        var errors = Config(new KubernetesClusterConfig("cluster-a", "pve99", Kubectl: "kubectl")).Validate();
        Assert.Empty(errors);
    }

    [Fact]
    public void BlankAliasPrefixNameAndNonPositiveAgeAreActionable()
    {
        var errors = Config(new KubernetesClusterConfig("", " ", Kubectl: "", MaxBackupAgeHours: 0)).Validate();
        Assert.Contains(errors, e => e.Contains("kubernetesClusters[0].name is empty"));
        Assert.Contains(errors, e => e.Contains("kubernetesClusters[0].alias is empty"));
        Assert.Contains(errors, e => e.Contains("kubernetesClusters[0].kubectl is empty"));
        Assert.Contains(errors, e => e.Contains("maxBackupAgeHours must be positive"));
    }

    [Fact]
    public void ClusterNamesMustBeUnique()
    {
        var errors = Config(new KubernetesClusterConfig("same", "one"), new KubernetesClusterConfig("same", "two")).Validate();
        Assert.Contains(errors, e => e.Contains("kubernetesClusters[1].name 'same' is duplicated"));
    }

    [Fact]
    public void VeleroNamespaceMustBeSafeForTheRemoteCommand()
    {
        var errors = Config(new KubernetesClusterConfig("cluster-a", "pve99", VeleroNamespace: "bad namespace")).Validate();
        Assert.Contains(errors, error => error.Contains("veleroNamespace 'bad namespace' is not a valid Kubernetes namespace"));
    }

    [Fact]
    public void ExplicitNullCannotDisableEveryKubernetesSurface()
    {
        var config = Config(new KubernetesClusterConfig("cluster-a", "pve99", VeleroNamespace: null, CheckWorkloads: false));
        Assert.Contains(config.Validate(), error => error.Contains("disables both Velero and workload checks"));
    }
}
