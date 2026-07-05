using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Pve;

namespace RestoreGuard.Tests;

public class PbsMaintenanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly PbsMaintenanceOptions Options = new("pve", TimeSpan.FromDays(7));

    private static IReadOnlyList<Finding> Run(DatastoreMaintenance info) =>
        new PbsMaintenanceCheck(info, Options).Evaluate(LabInventory.Empty with { CapturedAt = Now }).ToList();

    [Fact]
    public void GoldenFile_LiveDatastoreHasNeitherGcNorVerify()
    {
        // Real 2026-07-04 capture: GC never ran (upid null), zero verify jobs — both
        // discovered by the probe, contradicting the docs' "backups + GC" assumption.
        var gc = PbsMaintenanceProvider.ParseGcLastRun(Fixtures.Read("pbs-gc-status.json"));
        var verifyCount = PbsMaintenanceProvider.ParseVerifyJobCount(Fixtures.Read("pbs-verify-jobs.json"));

        Assert.Null(gc);
        Assert.Equal(0, verifyCount);

        var findings = Run(new DatastoreMaintenance("main", gc, verifyCount));
        Assert.Equal(
            ["pbs/gc-never-ran", "pbs/no-verify-jobs"],
            findings.Select(f => f.RuleId).Order(StringComparer.Ordinal).ToList());
        Assert.All(findings, f => Assert.Equal(Severity.Yellow, f.Severity));
    }

    [Fact]
    public void GoldenFile_CompletedVerificationParsesAsOk()
    {
        // Live capture after the first-ever verification of datastore 'main'
        // (2026-07-05, 4/4 groups, 0 errors). Note: needed `task list --all` —
        // without it PBS lists only running tasks.
        var (time, status) = PbsMaintenanceProvider.ParseLastVerify(Fixtures.Read("pbs-task-list.json"));

        Assert.NotNull(time);
        Assert.Equal("OK", status);
        Assert.Equal(new DateOnly(2026, 7, 5), DateOnly.FromDateTime(time!.Value.UtcDateTime));
    }

    [Fact]
    public void RunningVerificationWithoutEndtimeIsIgnored()
    {
        var (time, status) = PbsMaintenanceProvider.ParseLastVerify(
            """[{"worker_type":"verificationjob","starttime":1783256944,"upid":"UPID:..."}]""");

        Assert.Null(time);
        Assert.Null(status);
    }

    [Fact]
    public void UpidStartTimeIsParsedFromHexField()
    {
        // starttime is the 6th colon-field, hex seconds: 0x68680000 = 2026-07-04-ish.
        var json = """{"store":"main","upid":"UPID:pbs:00000FA0:00012345:00000001:6868199C:garbage_collection:main:root@pam:"}""";
        var gc = PbsMaintenanceProvider.ParseGcLastRun(json);

        Assert.NotNull(gc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(0x6868199C), gc);
    }

    [Fact]
    public void RecentGcAndFreshOkVerifyIsClean()
    {
        Assert.Empty(Run(new DatastoreMaintenance("main", Now.AddDays(-1), 1,
            VerifyLastRun: Now.AddHours(-10), VerifyLastStatus: "OK")));
    }

    [Fact]
    public void StaleGcIsYellow()
    {
        var findings = Run(new DatastoreMaintenance("main", Now.AddDays(-20), 1,
            VerifyLastRun: Now.AddHours(-10), VerifyLastStatus: "OK"));
        var finding = Assert.Single(findings);
        Assert.Equal(("pbs/gc-stale", Severity.Yellow), (finding.RuleId, finding.Severity));
    }

    [Fact]
    public void VerifyJobExistsButNeverCompleted_IsYellow()
    {
        var finding = Assert.Single(Run(new DatastoreMaintenance("main", Now.AddDays(-1), 1)));
        Assert.Equal(("pbs/verify-never-ran", Severity.Yellow), (finding.RuleId, finding.Severity));
    }

    [Fact]
    public void FailedVerification_IsRed()
    {
        var findings = Run(new DatastoreMaintenance("main", Now.AddDays(-1), 1,
            VerifyLastRun: Now.AddHours(-5), VerifyLastStatus: "WARNINGS: 3 chunks bad"));
        var finding = Assert.Single(findings);
        Assert.Equal(("pbs/verify-failed", Severity.Red), (finding.RuleId, finding.Severity));
        Assert.Contains("chunks bad", finding.Evidence);
    }

    [Fact]
    public void StaleVerification_IsYellow()
    {
        var findings = Run(new DatastoreMaintenance("main", Now.AddDays(-1), 1,
            VerifyLastRun: Now.AddDays(-5), VerifyLastStatus: "OK"));
        var finding = Assert.Single(findings);
        Assert.Equal(("pbs/verify-stale", Severity.Yellow), (finding.RuleId, finding.Severity));
    }
}
