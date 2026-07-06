using System.Text.Json;

namespace RestoreGuard.Providers.Pve;

using RestoreGuard.Core.Model;

/// <summary>One image backup from `pvesh get /nodes/{n}/storage/{storage}/content` —
/// the same shape for PBS datastores and vzdump dir storages; Tier tells them apart.</summary>
public sealed record PbsSnapshot(
    int Vmid,
    string Subtype,       // "lxc" or "qemu"
    DateTimeOffset Ctime,
    long Size,
    string? Notes,
    string Volid,
    string Format = "",   // pbs-ct / pbs-vm / vma.zst / tar.zst
    BackupTier Tier = BackupTier.PbsImage,
    // The node whose storage listing produced this snapshot: for a NON-shared
    // storage that is the box physically holding the bytes ("local" exists on
    // every node — the name alone can't say which disk). Stamped by the provider.
    string Node = "");

public static class PbsContentParser
{
    public static IReadOnlyList<PbsSnapshot> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var snapshots = new List<PbsSnapshot>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("content", out var c) && c.GetString() != "backup")
                continue;

            var volid = el.GetProperty("volid").GetString() ?? "";
            var subtype = el.TryGetProperty("subtype", out var st)
                ? st.GetString() ?? ""
                // Fallback: derive from the volid (PBS path segments or vzdump filename).
                : volid.Contains(":backup/ct/") || volid.Contains("vzdump-lxc-") ? "lxc" : "qemu";

            snapshots.Add(new PbsSnapshot(
                Vmid: el.GetProperty("vmid").GetInt32(),
                Subtype: subtype,
                Ctime: DateTimeOffset.FromUnixTimeSeconds(el.GetProperty("ctime").GetInt64()),
                Size: el.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                Notes: el.TryGetProperty("notes", out var n) ? n.GetString() : null,
                Volid: volid,
                Format: el.TryGetProperty("format", out var f) ? f.GetString() ?? "" : ""));
        }

        return snapshots;
    }
}
