using RestoreGuard.Checks;
using RestoreGuard.Cli;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers;
using RestoreGuard.Providers.Sqlite;

namespace RestoreGuard.Tests;

file sealed class FakeScanSsh(string stdout) : ISshProvider
{
    public List<string> Commands { get; } = [];

    public Task<SshResult> RunAsync(string hostAlias, string command, CancellationToken ct = default)
    {
        Commands.Add(command);
        return Task.FromResult(new SshResult(0, stdout, ""));
    }
}

public class SqliteHotCopyTests
{
    private static readonly LabInventory Empty = new(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero), [], [], []);

    [Fact]
    public async Task ScanIsRecursive_CappedAndRelative()
    {
        var ssh = new FakeScanSsh(Fixtures.Read("sqlite-wal-scan.txt"));

        var scan = await new SqliteBackupProvider(ssh).GetAsync(
            new SqliteBackupDirConfig("appdata", "lab98", "/backups/appdata"));

        var cmd = Assert.Single(ssh.Commands);
        // Recursive (no -maxdepth), both suffixes, relative paths, flood-capped.
        Assert.Contains("-name '*-wal' -o -name '*-shm'", cmd);
        Assert.Contains("-printf '%P\\n'", cmd);
        Assert.Contains("| head -50", cmd);
        Assert.Equal(6, scan.WalFiles.Count);
        Assert.Contains("vaultwarden/db.sqlite3-wal", scan.WalFiles);
    }

    [Fact]
    public void HotCopiedDatabases_AreRed_WithTheFilesNamed()
    {
        var check = new SqliteHotCopyCheck(
            [new SqliteHotCopyScan("appdata", "lab98", "/backups/appdata",
                SqliteBackupProvider.ParseScan(Fixtures.Read("sqlite-wal-scan.txt")))]);

        var finding = Assert.Single(check.Evaluate(Empty));
        Assert.Equal(("sqlite/hot-copy", Severity.Red), (finding.RuleId, finding.Severity));
        Assert.Contains("vaultwarden/db.sqlite3-wal", finding.Evidence);
        Assert.Contains("(+1 more)", finding.Evidence); // 6 hits, 5 shown
        Assert.Contains(".backup", finding.SuggestedAction);
    }

    [Fact]
    public void CleanTree_IsQuiet()
    {
        var check = new SqliteHotCopyCheck(
            [new SqliteHotCopyScan("appdata", "lab98", "/backups/appdata", [])]);

        Assert.Empty(check.Evaluate(Empty));
    }

    [Fact]
    public void EmptyFieldsAreCaughtWithFieldNames()
    {
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null,
            SqliteBackupDirs: [new SqliteBackupDirConfig("", "", "")]);

        var errors = config.Validate();

        Assert.Contains("sqliteBackupDirs[0].name is empty.", errors);
        Assert.Contains("sqliteBackupDirs[0].alias is empty.", errors);
        Assert.Contains("sqliteBackupDirs[0].path is empty.", errors);
    }
}
