using System.Text.Json;
using RestoreGuard.Cli;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Tests;

public class JsonReportWriterTests
{
    [Fact]
    public void PayloadShapeIsStable()
    {
        var now = new DateTimeOffset(2026, 7, 4, 23, 30, 0, TimeSpan.Zero);
        var finding = new Finding("db-backup/stale", Severity.Red, "svc", "lab55", "old", "fix it");
        var suppression = new Suppression("lab98", "zigbee2mqtt", "service-state/stopped", "bypass", new DateOnly(2026, 6, 29));
        var report = new Report(now, [finding], [], [suppression]);
        var inventory = new LabInventory(now,
            [new Service("svc", "lab55", ServiceKind.Container, "running", null, [], null)], [], []);

        var json = JsonReportWriter.Write(report, inventory, ["lab118: unreachable"]);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // These names are the machine-readable contract — additive changes only.
        Assert.Equal("red", root.GetProperty("overall").GetString());
        Assert.True(root.GetProperty("partial").GetBoolean());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("services").GetInt32());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("red").GetInt32());
        Assert.Equal("db-backup/stale",
            root.GetProperty("findings")[0].GetProperty("ruleId").GetString());
        Assert.Equal("zigbee2mqtt",
            root.GetProperty("activeSuppressions")[0].GetProperty("service").GetString());
        Assert.Equal("lab118: unreachable", root.GetProperty("providerErrors")[0].GetString());
    }
}
