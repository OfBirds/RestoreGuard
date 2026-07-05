namespace RestoreGuard.Core.Model;

/// <summary>The four tiers that exist in the lab (see docs/backup-topology.md) plus cloud-sync copies.</summary>
public enum BackupTier
{
    PbsImage,
    Vzdump,
    LogicalDb,
    ZfsSnapshot,
    CloudSync,
    FileBackup,
}

/// <summary>
/// A concrete backup output found on a target (a PBS snapshot, a dump file, a ZFS
/// snapshot, a cloud-sync run…). Status carries the producer's own verdict when it
/// has one ("ok", "failed", "disabled"); null means the producer reports none.
/// </summary>
public sealed record BackupArtifact(
    BackupTier Tier,
    string TargetService,
    string Location,
    DateTimeOffset Timestamp,
    long SizeBytes,
    string? Method,
    bool HasOffsiteCopy,
    string? Status = null);
