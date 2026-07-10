using System.Text.Json;
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
    public void PayloadMatchesGoldenSnapshot()
    {
        var (report, inventory, providerErrors) = Sample();

        // destinations = the connection ids the report is delivered to (reporting.json);
        // populated here so the golden documents the field for consumers like HCC.
        var json = JsonReportWriter.Write(report, inventory, providerErrors, ["folder", "s3:backups"]);

        // A FULL snapshot, not a spot-check: any renamed/removed/retyped/reordered field
        // fails here — which is exactly the drift a downstream consumer would otherwise
        // discover only at runtime. Update the golden deliberately, and bump SchemaVersion
        // (+ ship a new contracts/*.schema.json) when the change is breaking.
        var golden = File.ReadAllText(Path.Combine("Fixtures", "report-golden.v1.json"));
        Assert.Equal(Normalize(golden), Normalize(json));
    }
}
