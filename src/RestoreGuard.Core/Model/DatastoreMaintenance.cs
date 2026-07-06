namespace RestoreGuard.Core.Model;

/// <summary>A proxmox-backup-client host backup ("host" type in the datastore):
/// the backup id (usually the hostname) and its newest snapshot, null = the id
/// has no snapshots at all.</summary>
public sealed record PbsHostBackup(string Id, DateTimeOffset? Newest);

/// <summary>Maintenance state of a backup datastore: when GC last ran (null = never),
/// how many scheduled verify jobs exist, the last completed verification's
/// time + outcome (null = never completed one), the same for PBS→PBS sync jobs,
/// and any watched proxmox-backup-client host backups.</summary>
public sealed record DatastoreMaintenance(
    string Datastore,
    DateTimeOffset? GcLastRun,
    int VerifyJobCount,
    DateTimeOffset? VerifyLastRun = null,
    string? VerifyLastStatus = null,
    int SyncJobCount = 0,
    DateTimeOffset? SyncLastRun = null,
    string? SyncLastStatus = null,
    IReadOnlyList<PbsHostBackup>? HostBackups = null);
