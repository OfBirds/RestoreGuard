using RestoreGuard.Core.Model;
using RestoreGuard.Providers.DbDump;

namespace RestoreGuard.Tests;

public class DumpListingParserTests
{
    [Fact]
    public void ParsesRealDumpDirectory()
    {
        var artifacts = DumpListingParser.Parse(
            Fixtures.Read("dump-listing-lab55.tsv"), "lab55:/var/backups/db-prod");

        // 2026-07-04 capture: 26 sql.gz dumps + 8 app-secrets tars.
        Assert.Equal(34, artifacts.Count);
        Assert.All(artifacts, a => Assert.Equal(BackupTier.LogicalDb, a.Tier));

        var mh = Assert.Single(artifacts, a => a.TargetService == "mulberryheron-prod-postgres-1");
        Assert.Equal(704922, mh.SizeBytes);
        Assert.Equal("pg_dumpall", mh.Method);
        Assert.Equal(new DateOnly(2026, 7, 4), DateOnly.FromDateTime(mh.Timestamp.UtcDateTime));

        var secrets = artifacts.Where(a => a.TargetService == "app-secrets").ToList();
        Assert.Equal(8, secrets.Count);
        Assert.All(secrets, a => Assert.Equal("tar", a.Method));
    }

    [Fact]
    public void FreshDumpRetentionWindowIsVisible()
    {
        var artifacts = DumpListingParser.Parse(
            Fixtures.Read("dump-listing-lab55.tsv"), "lab55:/var/backups/db-prod");

        var fuel = artifacts.Where(a => a.TargetService == "fuel-prod-postgres-1").ToList();
        Assert.Equal(8, fuel.Count); // 7-day retention + today's dump
        Assert.Equal(new DateOnly(2026, 7, 4),
            DateOnly.FromDateTime(fuel.Max(a => a.Timestamp).UtcDateTime));
    }
}
