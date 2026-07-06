using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

public sealed record PbsMaintenanceOptions(
    string Host,
    TimeSpan MaxGcAge,
    TimeSpan? MaxVerifyAge = null,
    TimeSpan? MaxSyncJobAge = null,
    TimeSpan? MaxHostBackupAge = null);

/// <summary>
/// PBS datastore hygiene: GC must actually run (without it, deleted snapshots never
/// free space and the datastore quietly fills), and verify jobs must exist (without
/// them, bit rot in chunks is discovered only at restore time — the exact disaster
/// RestoreGuard exists to prevent).
/// </summary>
public sealed class PbsMaintenanceCheck(DatastoreMaintenance info, PbsMaintenanceOptions options) : ICheck
{
    public string RuleId => "pbs-maintenance";

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        if (info.GcLastRun is null)
        {
            yield return new Finding(
                "pbs/gc-never-ran", Severity.Yellow, info.Datastore, options.Host,
                $"Garbage collection has never run on datastore '{info.Datastore}' (no GC task recorded).",
                "Schedule GC (prune alone does not reclaim space) — the datastore will grow until it silently fills.");
        }
        else
        {
            var age = inventory.CapturedAt - info.GcLastRun.Value;
            if (age > options.MaxGcAge)
            {
                yield return new Finding(
                    "pbs/gc-stale", Severity.Yellow, info.Datastore, options.Host,
                    $"Last GC on '{info.Datastore}' ran {age.TotalDays:F0} days ago (limit {options.MaxGcAge.TotalDays:F0}).",
                    "The GC schedule stopped firing — check the PBS task log.");
            }
        }

        // PBS→PBS sync jobs (auto-discovered — none configured on the PBS is fine;
        // the rclone offsite path covers non-PBS destinations).
        if (info.SyncJobCount > 0)
        {
            if (info.SyncLastRun is null)
            {
                yield return new Finding(
                    "pbs/sync-job-never-ran", Severity.Yellow, info.Datastore, options.Host,
                    $"{info.SyncJobCount} sync job(s) exist but no sync has ever completed.",
                    "Run the sync job once (or wait for its schedule) and re-audit.");
            }
            else if (info.SyncLastStatus is not null && info.SyncLastStatus != "OK")
            {
                yield return new Finding(
                    "pbs/sync-job-failed", Severity.Red, info.Datastore, options.Host,
                    $"The last completed PBS sync did NOT succeed: {info.SyncLastStatus}",
                    "Check the sync task log — until it succeeds again, the second PBS is behind.");
            }
            else
            {
                var syncAge = inventory.CapturedAt - info.SyncLastRun.Value;
                var maxSyncAge = options.MaxSyncJobAge ?? TimeSpan.FromHours(26);
                if (syncAge > maxSyncAge)
                {
                    yield return new Finding(
                        "pbs/sync-job-stale", Severity.Red, info.Datastore, options.Host,
                        $"Last successful PBS sync was {syncAge.TotalHours:F0}h ago (limit {maxSyncAge.TotalHours:F0}h).",
                        "The sync schedule stopped firing — check the PBS task log and the remote's reachability.");
                }
            }
        }

        // proxmox-backup-client host backups: every watched backup id must have a
        // fresh snapshot under host/ in the datastore.
        foreach (var hb in info.HostBackups ?? [])
        {
            if (hb.Newest is not { } newest)
            {
                yield return new Finding(
                    "pbs/host-backup-missing", Severity.Red, hb.Id, options.Host,
                    $"No host backups at all for '{hb.Id}' in datastore '{info.Datastore}'.",
                    "The proxmox-backup-client job on that host never ran (or backs up under a different id) — run it once and check its schedule.");
                continue;
            }

            var hostAge = inventory.CapturedAt - newest;
            var maxHostAge = options.MaxHostBackupAge ?? TimeSpan.FromHours(26);
            if (hostAge > maxHostAge)
            {
                yield return new Finding(
                    "pbs/host-backup-stale", Severity.Red, hb.Id, options.Host,
                    $"Newest host backup of '{hb.Id}' is {hostAge.TotalHours:F0}h old (limit {maxHostAge.TotalHours:F0}h).",
                    "The proxmox-backup-client schedule on that host stopped producing backups — check its timer/cron and log.");
            }
        }

        if (info.VerifyJobCount == 0)
        {
            yield return new Finding(
                "pbs/no-verify-jobs", Severity.Yellow, info.Datastore, options.Host,
                $"Datastore '{info.Datastore}' has no verify jobs — snapshot integrity is never checked.",
                "Add a scheduled verify job; corrupted chunks are otherwise found only during a real restore.");
            yield break;
        }

        if (info.VerifyLastRun is null)
        {
            yield return new Finding(
                "pbs/verify-never-ran", Severity.Yellow, info.Datastore, options.Host,
                $"A verify job exists for '{info.Datastore}' but no verification has ever completed.",
                "Run the verify job once (or wait for its schedule) and re-audit.");
            yield break;
        }

        if (info.VerifyLastStatus is not null && info.VerifyLastStatus != "OK")
        {
            yield return new Finding(
                "pbs/verify-failed", Severity.Red, info.Datastore, options.Host,
                $"The last completed verification of '{info.Datastore}' did NOT pass: {info.VerifyLastStatus}",
                "Corrupted or unreadable chunks were found — identify the affected snapshots in the PBS task log and re-back-up those guests now.");
        }

        var verifyAge = inventory.CapturedAt - info.VerifyLastRun.Value;
        var maxVerifyAge = options.MaxVerifyAge ?? TimeSpan.FromHours(50);
        if (verifyAge > maxVerifyAge)
        {
            yield return new Finding(
                "pbs/verify-stale", Severity.Yellow, info.Datastore, options.Host,
                $"Last completed verification of '{info.Datastore}' was {verifyAge.TotalHours:F0}h ago (limit {maxVerifyAge.TotalHours:F0}h).",
                "The verify schedule stopped firing — check the PBS task log.");
        }
    }
}
