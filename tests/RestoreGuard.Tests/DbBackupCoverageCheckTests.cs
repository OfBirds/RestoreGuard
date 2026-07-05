using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.DbDump;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Tests;

public class DbBackupCoverageCheckTests
{
    // Noon UTC on capture day: the 02:00 dumps are hours old, well inside 26h.
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    private static readonly DbBackupCoverageOptions Options =
        new(["lab55"], TimeSpan.FromHours(26));

    private static Service Db(string name, string image, string state = "running", string host = "lab55") =>
        new(name, host, ServiceKind.Container, state, null, [], image);

    private static BackupArtifact Dump(string target, DateTimeOffset ts, long size) =>
        new(BackupTier.LogicalDb, target, $"lab55:/var/backups/db-prod/{target}.sql.gz",
            ts, size, "pg_dumpall", false);

    [Fact]
    public void GoldenFile_RealLabCatchesDocumentDbMethodMismatch()
    {
        // Assemble the inventory exactly as the CLI does, from the real captured DTOs.
        var containers = DockerInspectParser.Parse(Fixtures.Read("docker-inspect-lab55.json"));
        var services = ServiceAssembler.Build("lab55", containers, new Dictionary<string, ComposeProjectConfig?>());
        var backups = DumpListingParser.Parse(Fixtures.Read("dump-listing-lab55.tsv"), "lab55:/var/backups/db-prod");
        var inventory = new LabInventory(Now, services, backups, []);

        var findings = new DbBackupCoverageCheck(Options).Evaluate(inventory).ToList();

        // The one real problem in the capture: mulberryheron-prod runs DocumentDB but
        // gets pg_dumpall'd — predicted as a latent risk in docs/backup-topology.md,
        // live in prod since ~2026-07-03.
        var finding = Assert.Single(findings);
        Assert.Equal("db-backup/method-mismatch", finding.RuleId);
        Assert.Equal(Severity.Red, finding.Severity);
        Assert.Equal("mulberryheron-prod-postgres-1", finding.Service);
        // crimsonraven-kc-prod (fresh valid dump) is clean; fuel-postgres is exited (skipped).
    }

    [Fact]
    public void GoldenFile_MariadbJobOnLab98_CleanWhenFreshAndRightMethod()
    {
        // 2026-07-05 capture of the new mysqldump job on lab98 (dumps ~11:02Z).
        var now = new DateTimeOffset(2026, 7, 5, 18, 0, 0, TimeSpan.Zero);
        var backups = DumpListingParser.Parse(
            Fixtures.Read("dump-listing-lab98.tsv"), "lab98:/var/backups/db-98", sqlMethod: "mysqldump");
        Assert.All(backups, b => Assert.Equal("mysqldump", b.Method));

        string[] names = ["seafile-db", "photoprism-db_al", "photoprism-db_at", "mariadb_npm", "mariadb_wp"];
        var services = names.Select(n => Db(n, "mariadb:10.11", host: "lab98")).ToList();
        services.Add(Db("brand-new-db", "mariadb:11", host: "lab98")); // no dump yet

        var options = new DbBackupCoverageOptions(["lab98"], TimeSpan.FromHours(26),
            Method: "mysqldump", RequireProdNaming: false);
        var findings = new DbBackupCoverageCheck(options)
            .Evaluate(new LabInventory(now, services, backups, [])).ToList();

        // All five real DBs are covered and method-correct; only the new one flags.
        var finding = Assert.Single(findings);
        Assert.Equal(("db-backup/uncovered", "brand-new-db"), (finding.RuleId, finding.Service));
    }

    [Fact]
    public void MariadbUnderPgDumpJob_IsMethodMismatch()
    {
        var inventory = new LabInventory(Now,
            [Db("cache-prod-mariadb-1", "mariadb:11")],
            [Dump("cache-prod-mariadb-1", Now.AddHours(-5), 9000)], []);

        var findings = new DbBackupCoverageCheck(Options).Evaluate(inventory).ToList();

        var mismatch = Assert.Single(findings, f => f.RuleId == "db-backup/method-mismatch");
        Assert.Contains("needs mysqldump", mismatch.Evidence);
    }

    [Fact]
    public void WithoutProdNaming_StagingDbsAreExpectedToo()
    {
        var options = new DbBackupCoverageOptions(["lab55"], TimeSpan.FromHours(26),
            RequireProdNaming: false);
        var inventory = new LabInventory(Now,
            [Db("app-staging-postgres-1", "postgres:17")], [], []);

        var finding = Assert.Single(new DbBackupCoverageCheck(options).Evaluate(inventory));
        Assert.Equal("db-backup/uncovered", finding.RuleId);
    }

    [Fact]
    public void UnmatchedRunningDbIsRed()
    {
        var inventory = new LabInventory(Now, [Db("adhoc-postgres", "postgres:16")], [], []);

        var finding = Assert.Single(new DbBackupCoverageCheck(Options).Evaluate(inventory));
        Assert.Equal("db-backup/unmatched", finding.RuleId);
        Assert.Equal(Severity.Red, finding.Severity);
    }

    [Fact]
    public void ProdDbWithoutAnyDumpIsRed()
    {
        var inventory = new LabInventory(Now, [Db("newapp-prod-postgres-1", "postgres:17")], [], []);

        var finding = Assert.Single(new DbBackupCoverageCheck(Options).Evaluate(inventory));
        Assert.Equal("db-backup/uncovered", finding.RuleId);
    }

    [Fact]
    public void StaleDumpIsRed()
    {
        var inventory = new LabInventory(Now,
            [Db("app-prod-postgres-1", "postgres:17")],
            [Dump("app-prod-postgres-1", Now.AddDays(-3), 5000)], []);

        var finding = Assert.Single(new DbBackupCoverageCheck(Options).Evaluate(inventory));
        Assert.Equal("db-backup/stale", finding.RuleId);
    }

    [Fact]
    public void ZeroByteDumpIsRed_TinyDumpIsYellow()
    {
        var empty = new LabInventory(Now,
            [Db("a-prod-postgres-1", "postgres:17")],
            [Dump("a-prod-postgres-1", Now.AddHours(-10), 0)], []);
        Assert.Equal("db-backup/empty",
            Assert.Single(new DbBackupCoverageCheck(Options).Evaluate(empty)).RuleId);

        var tiny = new LabInventory(Now,
            [Db("b-prod-postgres-1", "postgres:17")],
            [Dump("b-prod-postgres-1", Now.AddHours(-10), 1398)], []);
        var finding = Assert.Single(new DbBackupCoverageCheck(Options).Evaluate(tiny));
        Assert.Equal("db-backup/small", finding.RuleId);
        Assert.Equal(Severity.Yellow, finding.Severity);
    }

    [Fact]
    public void StagingStoppedAndUncoveredHostsAreSkipped()
    {
        var inventory = new LabInventory(Now,
            [
                Db("app-staging-postgres-1", "postgres:17"),
                Db("old-postgres", "postgres:15", state: "exited"),
                Db("seafile-db", "mariadb:10", host: "lab98"), // covered by PBS images, not Tier 3
            ],
            [], []);

        Assert.Empty(new DbBackupCoverageCheck(Options).Evaluate(inventory));
    }
}
