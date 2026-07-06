using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

/// <summary>
/// The classic SQLite backup failure: rsync/cp of a running app's data dir
/// "succeeds" daily while producing databases that restore corrupt. A `-wal` or
/// `-shm` file only exists next to an OPEN database — inside a backup directory
/// it is proof the copy happened mid-write. RED, because the whole point of that
/// backup is the database next to it. Scans are injected from discovery.
/// </summary>
public sealed class SqliteHotCopyCheck(IReadOnlyList<SqliteHotCopyScan> scans) : ICheck
{
    public string RuleId => "sqlite";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        foreach (var scan in scans)
        {
            if (scan.WalFiles.Count == 0)
                continue; // clean tree — the copies were made with the db closed (or properly dumped)

            var shown = string.Join(", ", scan.WalFiles.Take(5));
            var more = scan.WalFiles.Count > 5 ? $" (+{scan.WalFiles.Count - 5} more)" : "";
            yield return new Finding(
                "sqlite/hot-copy", Severity.Red, scan.Name, scan.Host,
                $"Backup dir '{scan.Path}' contains WAL/SHM files — the SQLite database(s) next to them "
                + $"were copied while OPEN and may not restore: {shown}{more}.",
                "Copy SQLite databases with `sqlite3 <db> '.backup <out>'` or `VACUUM INTO` (or stop the app "
                + "during the copy). A live-copied .db can be silently torn — don't trust it until re-made cold.");
        }
    }
}
