namespace RestoreGuard.Core.Model;

/// <summary>A pool, PBS datastore, dataset, or off-site remote that backups land on.</summary>
public sealed record StorageTarget(
    string Name,
    string Host,
    long CapacityBytes,
    long FreeBytes,
    string Health,
    DateTimeOffset? LastScrubOrGc);
