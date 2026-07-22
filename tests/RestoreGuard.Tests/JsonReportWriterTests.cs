using System.Text.Json;
using System.Text.Json.Nodes;
using RestoreGuard.Cli;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Tests;

public class JsonReportWriterTests
{
    // The fixed inputs whose serialization is frozen in Fixtures/report-golden.v1.json.
    // Deterministic on purpose (fixed timestamp, no ambient state) so the output is a
    // stable snapshot of the machine-readable contract.
    private static (Report report, LabInventory inventory, string[] providerErrors) Sample()
    {
        var now = new DateTimeOffset(2026, 7, 4, 23, 30, 0, TimeSpan.Zero);
        var finding = new Finding("db-backup/stale", Severity.Red, "svc", "lab55", "old", "fix it");
        var suppression = new Suppression("lab98", "zigbee2mqtt", "service-state/stopped", "bypass", new DateOnly(2026, 6, 29));
        var report = new Report(now, [finding], [], [suppression]);
        var inventory = new LabInventory(now,
            [new Service("svc", "lab55", ServiceKind.Container, "running", null, [], null)], [], []);
        return (report, inventory, ["lab118: unreachable"]);
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    [Fact]
    public void SchemaVersionIsStamped()
    {
        var (report, inventory, providerErrors) = Sample();

        var json = JsonReportWriter.Write(report, inventory, providerErrors);

        using var doc = JsonDocument.Parse(json);
        // Downstream consumers (HCC) key off this to pick the right reader; it must be
        // the first thing they can trust. Constant per JsonReportWriter.SchemaVersion.
        Assert.Equal(JsonReportWriter.SchemaVersion, doc.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void HashIsStableAcrossTargets()
    {
        var (report, inventory, providerErrors) = Sample();
        var a = JsonDocument.Parse(JsonReportWriter.Write(report, inventory, providerErrors, "folder")).RootElement;
        var b = JsonDocument.Parse(JsonReportWriter.Write(report, inventory, providerErrors, "s3:backups")).RootElement;

        // Same audit -> same hash regardless of target (so a reader dedups across targets);
        // target differs per copy.
        Assert.Equal(a.GetProperty("hash").GetString(), b.GetProperty("hash").GetString());
        Assert.Equal("folder", a.GetProperty("target").GetString());
        Assert.Equal("s3:backups", b.GetProperty("target").GetString());
    }

    [Fact]
    public void PayloadMatchesGoldenSnapshot()
    {
        var (report, inventory, providerErrors) = Sample();

        var json = JsonReportWriter.Write(report, inventory, providerErrors, target: "offsite-minio");

        // A FULL snapshot, not a spot-check: any renamed/removed/retyped field fails
        // here — which is exactly the drift a downstream consumer would otherwise
        // discover only at runtime. Field *order* does not matter (System.Text.Json
        // property emission order varies across SDK versions); the golden is compared
        // structurally, not as a raw string.
        // Update the golden deliberately, and bump SchemaVersion (+ ship a new
        // contracts/*.schema.json) when the change is breaking.
        var path = Path.Combine("Fixtures", "report-golden.v1.json");
        if (Environment.GetEnvironmentVariable("RG_UPDATE_TRANSCRIPTS") == "1")
        {
            File.WriteAllText(path, json);
            return;
        }

        var expected = JsonNode.Parse(File.ReadAllText(path));
        var actual = JsonNode.Parse(json);
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            "Golden snapshot differs from actual output. Fields may have been renamed, removed, " +
            "retyped, or given new values. Regenerate with RG_UPDATE_TRANSCRIPTS=1 and bump " +
            "SchemaVersion if the change is breaking.");
    }
}
