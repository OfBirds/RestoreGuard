using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers.Sqlite;

/// <summary>One rsync/plain-copy backup directory to scan for hot-copied SQLite
/// databases (vaultwarden, the *arrs, Home Assistant, paperless — half of
/// selfhosted runs on SQLite).</summary>
public sealed record SqliteBackupDirConfig(string Name, string Alias, string Path);

/// <summary>
/// Read-only scan of a backup directory tree for `*-wal` / `*-shm` files. SQLite
/// creates those next to a database only while it is open — finding one INSIDE a
/// backup means the .db was copied live (rsync/cp of a running app's data dir),
/// the classic silently-corrupt-restore failure.
/// </summary>
public sealed class SqliteBackupProvider(ISshProvider ssh)
{
    public async Task<SqliteHotCopyScan> GetAsync(SqliteBackupDirConfig config, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync(config.Alias, ScanCommand(config.Path), ct);
        if (result.ExitCode != 0)
            throw new ProviderException($"'{ScanCommand(config.Path)}' on {config.Alias} failed: {result.StdErr.Trim()}");
        return new SqliteHotCopyScan(config.Name, config.Alias, config.Path, ParseScan(result.StdOut));
    }

    /// <summary>Recursive by design — rsync-style backups mirror whole app-data
    /// trees. Capped so a pathological tree can't flood the report.</summary>
    public static string ScanCommand(string path) =>
        $"find '{path}' \\( -name '*-wal' -o -name '*-shm' \\) -type f -printf '%P\\n' | head -50";

    public static IReadOnlyList<string> ParseScan(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
