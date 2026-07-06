namespace RestoreGuard.Core.Model;

/// <summary>
/// Where a backup-bearing storage physically lives: the node that reported it and
/// whether it is shared (mounted cluster-wide) or local to that node. Shared
/// storages appear once per node that mounts them — consumers treat any Shared
/// entry for a name as "this storage is not tied to one box".
/// </summary>
public sealed record StorageLocality(string Name, string Node, bool Shared);
