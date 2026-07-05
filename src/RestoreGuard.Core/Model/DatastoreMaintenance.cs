namespace RestoreGuard.Core.Model;

/// <summary>Maintenance state of a backup datastore: when GC last ran (null = never),
/// how many scheduled verify jobs exist, and the last completed verification's
/// time + outcome (null = never completed one).</summary>
public sealed record DatastoreMaintenance(
    string Datastore,
    DateTimeOffset? GcLastRun,
    int VerifyJobCount,
    DateTimeOffset? VerifyLastRun = null,
    string? VerifyLastStatus = null);
