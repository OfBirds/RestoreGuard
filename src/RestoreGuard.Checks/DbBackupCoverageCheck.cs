using System.Text.RegularExpressions;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

/// <summary>Options for one dump job. Method is what the job runs (pg_dumpall,
/// mysqldump, …). RequireProdNaming mirrors jobs that only dump containers named
/// 'prod' (and skip 'staging'); when false, every running DB container on the
/// covered hosts is expected to have a dump.</summary>
public sealed record DbBackupCoverageOptions(
    IReadOnlyList<string> CoveredHosts,
    TimeSpan MaxDumpAge,
    string Method = "pg_dumpall",
    bool RequireProdNaming = true,
    long SuspiciouslySmallBytes = 2048);

/// <summary>
/// Cross-checks live DB containers against one logical-dump job's output. Beyond
/// existence/freshness/size, the dump METHOD must fit the engine: pg_dumpall against
/// a FerretDB/DocumentDB or MariaDB instance "succeeds" yet is unrestorable — the
/// exact silent failure this product exists to catch (and did catch, live).
/// One check instance per configured job.
/// </summary>
public sealed partial class DbBackupCoverageCheck(DbBackupCoverageOptions options) : ICheck
{
    public string RuleId => "db-backup";

    [GeneratedRegex("postgres|documentdb|mysql|mariadb|mongo", RegexOptions.IgnoreCase)]
    private static partial Regex DbImage();

    /// <summary>The dump method that validly captures this engine, or null when no
    /// supported dump method exists (a mismatch whatever the job runs).</summary>
    private static string? ExpectedMethod(string image)
    {
        if (image.Contains("documentdb", StringComparison.OrdinalIgnoreCase)
            || image.Contains("ferretdb", StringComparison.OrdinalIgnoreCase))
        {
            return null; // pg-wire lies about it; mongodump via FerretDB is the real path
        }
        if (image.Contains("mysql", StringComparison.OrdinalIgnoreCase)
            || image.Contains("mariadb", StringComparison.OrdinalIgnoreCase))
        {
            return "mysqldump";
        }
        if (image.Contains("mongo", StringComparison.OrdinalIgnoreCase))
        {
            return "mongodump";
        }
        return "pg_dumpall";
    }

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        var dumps = inventory.Backups
            .Where(b => b.Tier == BackupTier.LogicalDb && b.Method == options.Method)
            .ToLookup(b => b.TargetService, StringComparer.Ordinal);

        var dbContainers = inventory.Services.Where(s =>
            options.CoveredHosts.Contains(s.Host, StringComparer.OrdinalIgnoreCase)
            && s.State == "running"
            && s.Image is not null && DbImage().IsMatch(s.Image));

        foreach (var db in dbContainers)
        {
            if (options.RequireProdNaming)
            {
                if (db.Name.Contains("staging", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!db.Name.Contains("prod", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new Finding(
                        "db-backup/unmatched", Severity.Red, db.Name, db.Host,
                        $"Running DB container '{db.Name}' ({db.Image}) is neither staging nor prod by name — the dump job classifies it as unmatched and never dumps it.",
                        "Rename to the prod convention so the backup job picks it up, or suppress with a reason if it is intentionally not backed up.");
                    continue;
                }
            }

            var latest = dumps[db.Name].OrderByDescending(b => b.Timestamp).FirstOrDefault();
            if (latest is null)
            {
                yield return new Finding(
                    "db-backup/uncovered", Severity.Red, db.Name, db.Host,
                    $"DB container '{db.Name}' has no logical dump at all in the backup directory.",
                    "Verify the dump job discovers this container; run it manually and check for errors.");
                continue;
            }

            var age = inventory.CapturedAt - latest.Timestamp;
            if (age > options.MaxDumpAge)
            {
                yield return new Finding(
                    "db-backup/stale", Severity.Red, db.Name, db.Host,
                    $"Latest dump {latest.Location} is {age.TotalHours:F0}h old (limit {options.MaxDumpAge.TotalHours:F0}h).",
                    "The dump job stopped producing output for this DB — check its log.");
            }

            if (latest.SizeBytes == 0)
            {
                yield return new Finding(
                    "db-backup/empty", Severity.Red, db.Name, db.Host,
                    $"Latest dump {latest.Location} is 0 bytes — the classic silent empty backup.",
                    "The dump command is producing nothing. Restore-test immediately; fix the dump pipeline.");
            }
            else if (latest.SizeBytes < options.SuspiciouslySmallBytes)
            {
                yield return new Finding(
                    "db-backup/small", Severity.Yellow, db.Name, db.Host,
                    $"Latest dump {latest.Location} is only {latest.SizeBytes} bytes — plausible for a near-empty DB, but verify.",
                    "Confirm the DB is genuinely this small (new app?) — if not, the dump is truncated.");
            }

            var expected = ExpectedMethod(db.Image!);
            if (expected != options.Method)
            {
                yield return new Finding(
                    "db-backup/method-mismatch", Severity.Red, db.Name, db.Host,
                    expected is null
                        ? $"'{db.Name}' runs a FerretDB/DocumentDB image ({db.Image}) but is backed up with {options.Method} — the dump 'succeeds' yet does not validly capture a DocumentDB instance."
                        : $"'{db.Name}' ({db.Image}) needs {expected}, but this job dumps with {options.Method} — the output is not a valid backup for this engine.",
                    expected is null
                        ? "Back this DB up with a method that understands DocumentDB (e.g. mongodump via FerretDB, or a filesystem-consistent snapshot)."
                        : $"Point a {expected}-based job at this container (or move it to one).");
            }
        }
    }
}
