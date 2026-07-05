using System.Text.Json;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Providers.Pve;

/// <summary>A VM or LXC guest from `pvesh get /cluster/resources`.</summary>
public sealed record PveGuest(int Vmid, string Name, string Node, ServiceKind Kind, string Status)
{
    public Service ToService() =>
        new(Name, Node, Kind, Status, DeclaredMounts: null, LiveMounts: [], Image: null);
}

public static class PveResourcesParser
{
    public static IReadOnlyList<PveGuest> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var guests = new List<PveGuest>();

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var type = el.GetProperty("type").GetString();
            if (type is not ("qemu" or "lxc"))
                continue;
            if (el.TryGetProperty("template", out var tmpl) && tmpl.GetInt32() == 1)
                continue;

            guests.Add(new PveGuest(
                Vmid: el.GetProperty("vmid").GetInt32(),
                Name: el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Node: el.GetProperty("node").GetString() ?? "",
                Kind: type == "qemu" ? ServiceKind.Vm : ServiceKind.Lxc,
                Status: el.GetProperty("status").GetString() ?? "unknown"));
        }

        return guests;
    }
}
