using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Tests;

public class StorageCapacityCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly StorageCapacityCheck Check = new(new StorageCapacityOptions());

    private static LabInventory With(params StorageTarget[] storage) => new(Now, [], [], storage);

    private static StorageTarget Storage(long capacity, long free, string health = "available") =>
        new("pbs-nvme", "pve", capacity, free, health, null);

    [Fact]
    public void HealthyStorageIsClean() =>
        Assert.Empty(Check.Evaluate(With(Storage(1000, 640))));

    [Fact]
    public void Above80PercentIsYellow()
    {
        var finding = Assert.Single(Check.Evaluate(With(Storage(1000, 150))));
        Assert.Equal(("storage/capacity-warn", Severity.Yellow), (finding.RuleId, finding.Severity));
    }

    [Fact]
    public void Above95PercentIsRed()
    {
        var finding = Assert.Single(Check.Evaluate(With(Storage(1000, 30))));
        Assert.Equal(("storage/capacity-critical", Severity.Red), (finding.RuleId, finding.Severity));
    }

    [Fact]
    public void InactiveStorageIsYellow()
    {
        var finding = Assert.Single(Check.Evaluate(With(Storage(1000, 900, health: "inactive"))));
        Assert.Equal("storage/inactive", finding.RuleId);
    }

    [Fact]
    public void OnlinePoolIsHealthy() =>
        Assert.Empty(Check.Evaluate(With(Storage(1000, 640, health: "ONLINE"))));

    [Fact]
    public void DegradedPoolIsRed()
    {
        var finding = Assert.Single(Check.Evaluate(With(Storage(1000, 640, health: "DEGRADED"))));
        Assert.Equal(("storage/unhealthy", Severity.Red), (finding.RuleId, finding.Severity));
    }

    [Fact]
    public void OverdueScrubIsYellow()
    {
        var target = new StorageTarget("ssd_pool_one", "truenas", 1000, 640, "ONLINE", Now.AddDays(-60));
        var finding = Assert.Single(Check.Evaluate(With(target)));
        Assert.Equal(("storage/scrub-overdue", Severity.Yellow), (finding.RuleId, finding.Severity));
    }

    [Fact]
    public void RecentScrubIsClean()
    {
        var target = new StorageTarget("ssd_pool_one", "truenas", 1000, 640, "ONLINE", Now.AddDays(-20));
        Assert.Empty(Check.Evaluate(With(target)));
    }
}
