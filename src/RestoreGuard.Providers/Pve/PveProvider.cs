using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers.Pve;

/// <summary>One PVE node. BackupStorages names the storages to pull backup content
/// from — PBS datastores and vzdump dir storages alike (the storage's type decides
/// the artifact tier). List a shared PBS datastore on exactly ONE node.</summary>
public sealed record PveNodeConfig(string Alias, string Node, IReadOnlyList<string>? BackupStorages = null);

/// <summary>
/// Read-only PVE discovery via `pvesh` over SSH — no API token to provision, and the
/// output is the same JSON the REST API serves. A later REST client can swap in
/// behind the same parsers.
/// </summary>
public sealed class PveProvider(ISshProvider ssh)
{
    public sealed record NodeInventory(
        IReadOnlyList<PveGuest> Guests,
        IReadOnlyList<PveStorage> Storages,
        IReadOnlyList<PbsSnapshot> PbsSnapshots);

    public async Task<NodeInventory> GetNodeAsync(PveNodeConfig node, CancellationToken ct = default)
    {
        var resources = await RunJsonAsync(node.Alias, "pvesh get /cluster/resources --output-format json", ct);
        var storageJson = await RunJsonAsync(node.Alias, $"pvesh get /nodes/{node.Node}/storage --output-format json", ct);
        var storages = PveStorageParser.Parse(storageJson, node.Node);

        var snapshots = new List<PbsSnapshot>();
        foreach (var name in node.BackupStorages ?? [])
        {
            // The content API has the same shape for every storage type; the storage
            // type decides which tier the artifacts belong to.
            var tier = storages.FirstOrDefault(s => s.Target.Name == name)?.Type == "pbs"
                ? BackupTier.PbsImage
                : BackupTier.Vzdump;
            var content = await RunJsonAsync(
                node.Alias, $"pvesh get /nodes/{node.Node}/storage/{name}/content --output-format json", ct);
            snapshots.AddRange(PbsContentParser.Parse(content).Select(s => s with { Tier = tier, Node = node.Node }));
        }

        return new NodeInventory(PveResourcesParser.Parse(resources), storages, snapshots);
    }

    /// <summary>Dedups shared storages (the PBS datastore is reported by every node).</summary>
    public static IReadOnlyList<StorageTarget> MergeStorages(IEnumerable<PveStorage> storages)
    {
        var seenShared = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<StorageTarget>();
        foreach (var s in storages)
        {
            if (s.Shared && !seenShared.Add(s.Target.Name))
                continue;
            result.Add(s.Target);
        }
        return result;
    }

    private async Task<string> RunJsonAsync(string alias, string command, CancellationToken ct)
    {
        var result = await ssh.RunAsync(alias, command, ct);
        if (result.ExitCode != 0)
            throw new ProviderException($"'{command}' on {alias} failed: {result.StdErr.Trim()}");
        return result.StdOut;
    }
}
