using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Tests;

public class CheckEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedCheck(params Finding[] findings) : ICheck
    {
        public string RuleId => "test-rule";
        public IEnumerable<Finding> Evaluate(LabInventory inventory) => findings;
    }

    private static Finding RedFinding(string service = "vaultwarden", string host = "lab98") =>
        new("test-rule", Severity.Red, service, host, "evidence", "action");

    private static LabInventory InventoryWith(params string[] serviceNames) =>
        new(Now,
            serviceNames.Select(n => new Service(n, "lab98", ServiceKind.Container, "running", null, [], null)).ToList(),
            [], []);

    [Fact]
    public void EmptyInventoryIsGreen()
    {
        var report = new CheckEngine([]).Run(LabInventory.Empty, [], Now);

        Assert.Equal(Severity.Green, report.Overall);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void UnsuppressedFindingSurfacesAndDrivesOverall()
    {
        var engine = new CheckEngine([new FixedCheck(RedFinding())]);

        var report = engine.Run(LabInventory.Empty, [], Now);

        Assert.Equal(Severity.Red, report.Overall);
        Assert.Single(report.Findings);
        Assert.Empty(report.SuppressedFindings);
    }

    [Fact]
    public void MatchingSuppressionMovesFindingToSuppressedBucket()
    {
        var engine = new CheckEngine([new FixedCheck(RedFinding())]);
        var suppression = new Suppression("lab98", "vaultwarden", "test-rule", "accepted", new DateOnly(2026, 6, 29));

        var report = engine.Run(InventoryWith("vaultwarden"), [suppression], Now);

        Assert.Equal(Severity.Green, report.Overall);
        Assert.Empty(report.Findings);
        Assert.Single(report.SuppressedFindings);
        Assert.Single(report.ActiveSuppressions);
    }

    [Fact]
    public void ExpiredSuppressionNoLongerHidesFindingAndFailsLoud()
    {
        var engine = new CheckEngine([new FixedCheck(RedFinding())]);
        var expired = new Suppression("lab98", "vaultwarden", "test-rule", "accepted",
            new DateOnly(2026, 1, 1), Expires: new DateOnly(2026, 6, 1));

        var report = engine.Run(InventoryWith("vaultwarden"), [expired], Now);

        Assert.Equal(Severity.Red, report.Overall);
        Assert.Equal(2, report.Findings.Count);
        Assert.Contains(report.Findings, f => f.RuleId == "test-rule");
        // The rotting entry itself is a finding — expired suppressions must not linger.
        Assert.Contains(report.Findings, f => f.RuleId == "suppression/expired");
        Assert.Empty(report.SuppressedFindings);
    }

    [Fact]
    public void SuppressionForOtherServiceDoesNotMatch()
    {
        var engine = new CheckEngine([new FixedCheck(RedFinding(service: "gitea"))]);
        var suppression = new Suppression("lab98", "vaultwarden", "test-rule", "accepted", new DateOnly(2026, 6, 29));

        var report = engine.Run(InventoryWith("gitea", "vaultwarden"), [suppression], Now);

        Assert.Single(report.Findings);
        Assert.Empty(report.SuppressedFindings);
    }

    [Fact]
    public void SuppressionWhoseTargetVanishedFailsLoud()
    {
        // The Zitadel->Keycloak rot: the suppressed container no longer exists.
        var suppression = new Suppression("lab55", "crimson-raven-zitadel-postgres",
            "db-backup/unmatched", "was staging zitadel", new DateOnly(2026, 6, 29));

        var report = new CheckEngine([]).Run(InventoryWith("crimsonraven-kc-prod-postgres-1"), [suppression], Now);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(("suppression/unknown-target", Severity.Yellow), (finding.RuleId, finding.Severity));
    }

    [Fact]
    public void SuppressionTargetsCanBeStorageOrComposeProjects()
    {
        var inventory = new LabInventory(Now,
            [new Service("telegraf", "lab98", ServiceKind.Container, "running", null, [], null, ComposeProject: "zigbee")],
            [],
            [new StorageTarget("ssd_pool_one/photos", "truenas", 1, 1, "available", null)]);

        List<Suppression> suppressions =
        [
            new("truenas", "ssd_pool_one/photos", "cloudsync/not-off-box", "accepted", new DateOnly(2026, 6, 29)),
            new("lab98", "zigbee", "mount-drift/unresolved-declared", "portainer stack", new DateOnly(2026, 6, 29)),
        ];

        var report = new CheckEngine([]).Run(inventory, suppressions, Now);

        Assert.Empty(report.Findings);
    }
}
