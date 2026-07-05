using System.Text.Json;
using System.Text.Json.Serialization;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Cli;

/// <summary>
/// The stable machine-readable report: everything a script or dashboard needs,
/// on stdout. Field names are a contract — additive changes only.
/// </summary>
public static class JsonReportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Write(Report report, LabInventory inventory, IReadOnlyList<string> providerErrors)
    {
        var payload = new
        {
            generatedAt = report.GeneratedAt,
            overall = report.Overall,
            partial = providerErrors.Count > 0,
            counts = new
            {
                services = inventory.Services.Count,
                backupArtifacts = inventory.Backups.Count,
                storageTargets = inventory.Storage.Count,
                red = report.Findings.Count(f => f.Severity == Severity.Red),
                yellow = report.Findings.Count(f => f.Severity == Severity.Yellow),
            },
            findings = report.Findings,
            suppressedFindings = report.SuppressedFindings,
            activeSuppressions = report.ActiveSuppressions,
            providerErrors,
        };
        return JsonSerializer.Serialize(payload, Options);
    }
}
