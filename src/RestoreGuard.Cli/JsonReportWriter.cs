using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private static readonly JsonSerializerOptions CompactOptions = new(Options) { WriteIndented = false };

    /// <summary>
    /// Serializes the report. <paramref name="target"/> is the connection id this copy is
    /// written to (null for stdout). <c>hash</c> is the SHA-256 of the report core (every
    /// field except <c>target</c> and <c>hash</c>), so all copies of one audit share a hash
    /// and a reader can dedup across targets.
    /// </summary>
    public static string Write(Report report, LabInventory inventory, IReadOnlyList<string> providerErrors,
        string? target = null)
    {
        var core = new
        {
            schemaVersion = SchemaVersion,
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

        var node = JsonSerializer.SerializeToNode(core, Options)!.AsObject();
        var hash = AwsSigV4.Sha256Hex(Encoding.UTF8.GetBytes(node.ToJsonString(CompactOptions)));
        node["target"] = target;
        node["hash"] = hash;
        return node.ToJsonString(Options);
    }
}
