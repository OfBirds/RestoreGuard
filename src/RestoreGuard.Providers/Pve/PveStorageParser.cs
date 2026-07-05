using System.Text.Json;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Providers.Pve;

/// <summary>
/// A storage from `pvesh get /nodes/{node}/storage`. Shared storages (the PBS
/// datastore) appear on every node that mounts them — the caller dedups by name.
/// </summary>
public sealed record PveStorage(StorageTarget Target, bool Shared, string Type = "");

public static class PveStorageParser
{
    public static IReadOnlyList<PveStorage> Parse(string json, string node)
    {
        using var doc = JsonDocument.Parse(json);
        var storages = new List<PveStorage>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var active = el.TryGetProperty("active", out var a) && a.GetInt32() == 1;
            storages.Add(new PveStorage(
                new StorageTarget(
                    Name: el.GetProperty("storage").GetString() ?? "",
                    Host: node,
                    CapacityBytes: el.TryGetProperty("total", out var t) ? t.GetInt64() : 0,
                    FreeBytes: el.TryGetProperty("avail", out var f) ? f.GetInt64() : 0,
                    Health: active ? "available" : "inactive",
                    LastScrubOrGc: null),
                Shared: el.TryGetProperty("shared", out var s) && s.GetInt32() == 1,
                Type: el.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : ""));
        }

        return storages;
    }
}
