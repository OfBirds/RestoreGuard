using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Tests;

public class MountDriftCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private static Service ComposeService(
        IReadOnlyList<MountSpec>? declared, IReadOnlyList<MountSpec> live) =>
        new("vaultwarden", "lab98", ServiceKind.Container, "running",
            declared, live, "vaultwarden/server:latest", ComposeProject: "vaultwarden");

    private static LabInventory Inventory(params Service[] services) =>
        new(Now, services, [], []);

    [Fact]
    public void MatchingDeclaredAndLiveIsClean()
    {
        var mount = new MountSpec("/opt/vaultwarden/data", "/data", false);
        var findings = new MountDriftCheck().Evaluate(Inventory(ComposeService([mount], [mount])));

        Assert.Empty(findings);
    }

    [Fact]
    public void RenamedBindMountIsRedDrift()
    {
        // The flagship failure: compose refactor renamed the host path, container never
        // recreated — backups now archive the new (empty) declared path.
        var declared = new MountSpec("/opt/vaultwarden-new/data", "/data", false);
        var live = new MountSpec("/opt/vaultwarden/data", "/data", false);

        var finding = Assert.Single(
            new MountDriftCheck().Evaluate(Inventory(ComposeService([declared], [live]))));

        Assert.Equal("mount-drift/source-mismatch", finding.RuleId);
        Assert.Equal(Severity.Red, finding.Severity);
        Assert.Contains("/opt/vaultwarden-new/data", finding.Evidence);
        Assert.Contains("/opt/vaultwarden/data", finding.Evidence);
    }

    [Fact]
    public void DeclaredMountAbsentLiveIsRed()
    {
        var declared = new MountSpec("/opt/vaultwarden/data", "/data", false);

        var finding = Assert.Single(
            new MountDriftCheck().Evaluate(Inventory(ComposeService([declared], []))));

        Assert.Equal("mount-drift/missing-live", finding.RuleId);
        Assert.Equal(Severity.Red, finding.Severity);
    }

    [Fact]
    public void UndeclaredLiveBindIsYellow()
    {
        var live = new MountSpec("/etc/localtime", "/etc/localtime", true);

        var finding = Assert.Single(
            new MountDriftCheck().Evaluate(Inventory(ComposeService([], [live]))));

        Assert.Equal("mount-drift/extra-live", finding.RuleId);
        Assert.Equal(Severity.Yellow, finding.Severity);
    }

    [Fact]
    public void AnonymousImageVolumeIsIgnored()
    {
        var anon = new MountSpec(new string('a', 64), "/var/lib/postgresql/data", false);
        var findings = new MountDriftCheck().Evaluate(Inventory(ComposeService([], [anon])));

        Assert.Empty(findings);
    }

    [Fact]
    public void UnresolvableComposeProjectIsYellowNotSilent()
    {
        var finding = Assert.Single(
            new MountDriftCheck().Evaluate(Inventory(ComposeService(null, []))));

        Assert.Equal("mount-drift/unresolved-declared", finding.RuleId);
        Assert.Equal(Severity.Yellow, finding.Severity);
        // Aggregated per project: the finding is attributed to the compose project.
        Assert.Equal("vaultwarden", finding.Service);
    }

    [Fact]
    public void UnresolvableProjectIsOneFindingForAllItsContainers()
    {
        var a = new Service("zigbee2mqtt", "lab98", ServiceKind.Container, "running",
            null, [], "koenkk/zigbee2mqtt", ComposeProject: "zigbee");
        var b = new Service("telegraf", "lab98", ServiceKind.Container, "running",
            null, [], "telegraf:latest", ComposeProject: "zigbee");

        var finding = Assert.Single(new MountDriftCheck().Evaluate(Inventory(a, b)));
        Assert.Equal("zigbee", finding.Service);
        Assert.Contains("zigbee2mqtt", finding.Evidence);
        Assert.Contains("telegraf", finding.Evidence);
    }

    [Fact]
    public void NonComposeContainerIsSkipped()
    {
        var standalone = new Service("portainer", "lab98", ServiceKind.Container, "running",
            null, [new MountSpec("/var/run/docker.sock", "/var/run/docker.sock", false)],
            "portainer/portainer-ce", ComposeProject: null);

        Assert.Empty(new MountDriftCheck().Evaluate(Inventory(standalone)));
    }
}
