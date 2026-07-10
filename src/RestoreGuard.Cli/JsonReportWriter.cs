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
    /// <summary>
    /// Version of the report CONTRACT (this JSON shape), deliberately NOT tied to
    /// RestoreGuard's product/package version. Bumps ONLY on a breaking change to
    /// the shape (a field renamed, removed, retyped, or given new meaning);
    /// additive changes (a new field) leave it untouched. The canonical schema for
    /// each version lives in <c>contracts/restoreguard-report.v{N}.schema.json</c>
    /// and is the contract of record for downstream consumers (e.g. HCC).
    /// </summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Write(Report report, LabInventory inventory, IReadOnlyList<string> providerErrors,
        IReadOnlyList<string>? destinations = null)
    {
        var payload = new
        {
            schemaVersion = SchemaVersion,
            generatedAt = report.GeneratedAt,
            overall = report.Overall,
            partial = providerErrors.Count > 0,
            // The connection ids this report is being delivered to (reporting.json),
            // so a report is self-describing about where it lives — the link a reader
            // (e.g. HCC) uses to match a report back to its destination in reporting.json.
            destinations = destinations ?? [],
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
