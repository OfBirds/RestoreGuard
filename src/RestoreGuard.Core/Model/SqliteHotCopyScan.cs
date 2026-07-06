namespace RestoreGuard.Core.Model;

/// <summary>
/// The result of scanning one backup directory for hot-copied SQLite databases:
/// every `*-wal` / `*-shm` file found (paths relative to the scanned root).
/// Those files only exist next to a database that is OPEN — inside a backup
/// directory they prove the db file was copied mid-write and may not restore.
/// </summary>
public sealed record SqliteHotCopyScan(
    string Name,
    string Host,
    string Path,
    IReadOnlyList<string> WalFiles);
