using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.S3;

namespace RestoreGuard.Tests;

public class S3ImmutabilityCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 4, 0, 0, TimeSpan.Zero);

    private static LabInventory Inventory() => new(Now, [], [], []);

    private static S3BucketAudit Audit(
        bool versioning = true,
        bool lockEnabled = true,
        bool hasRetention = true,
        int? retentionDays = 30,
        S3ObjectSummary? newest = null,
        bool incomplete = false) => new(
        "offsite",
        "homelab-backups",
        new(versioning ? "Enabled" : "Suspended", false),
        lockEnabled ? new(true, hasRetention ? "COMPLIANCE" : null, retentionDays, null) : new(false, null, null, null),
        newest,
        incomplete);

    private static List<Finding> Findings(
        S3BucketAudit audit,
        bool objectLockRequired = true,
        int? minRetention = null,
        bool checkNewest = true,
        double maxAgeHours = 26,
        string? providerError = null) =>
        new S3ImmutabilityCheck(
            providerError is null ? [audit] : [],
            [new S3BucketExpectation(
                "offsite", objectLockRequired, minRetention, checkNewest, maxAgeHours, providerError)])
            .Evaluate(Inventory())
            .ToList();

    [Fact]
    public void DisabledVersioningAndLockAreRed()
    {
        var findings = Findings(Audit(versioning: false, lockEnabled: false));
        Assert.Contains(findings, f => f.RuleId == "s3/versioning-disabled" && f.Severity == Severity.Red);
        Assert.Contains(findings, f => f.RuleId == "s3/object-lock-disabled" && f.Severity == Severity.Red);
    }

    [Fact]
    public void RetentionBelowMinimumIsRed()
    {
        var findings = Findings(Audit(retentionDays: 7), minRetention: 30);
        Assert.Contains(findings, f => f.RuleId == "s3/object-lock-retention-too-short" && f.Severity == Severity.Red);
    }

    [Fact]
    public void StaleNewestObjectIsRed()
    {
        var newest = new S3ObjectSummary("backup/old.json", Now.AddHours(-27), 100);
        var findings = Findings(Audit(newest: newest));
        Assert.Contains(findings, f => f.RuleId == "s3/newest-object-stale" && f.Severity == Severity.Red);
        Assert.Contains("backup/old.json", Assert.Single(findings, f => f.RuleId == "s3/newest-object-stale").Evidence);
    }

    [Fact]
    public void IncompleteBoundedListingIsYellowNotFalseGreen()
    {
        var findings = Findings(Audit(incomplete: true));
        Assert.Contains(findings, f => f.RuleId == "s3/newest-object-unproven" && f.Severity == Severity.Yellow);
    }

    [Fact]
    public void ProviderErrorAndOptionalRulesAreHandled()
    {
        var errorFindings = Findings(Audit(), providerError: "403 Forbidden");
        Assert.Contains(errorFindings, f => f.RuleId == "s3/unreachable" && f.Severity == Severity.Red);

        var optionalFindings = Findings(Audit(lockEnabled: false), objectLockRequired: false, checkNewest: false);
        Assert.DoesNotContain(optionalFindings, f => f.RuleId == "s3/object-lock-disabled");
    }
}
