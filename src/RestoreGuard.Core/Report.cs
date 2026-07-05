namespace RestoreGuard.Core;

/// <summary>
/// The finished audit result. Machine-readable first; every presentation
/// renders from this.
/// </summary>
public sealed record Report(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Finding> SuppressedFindings,
    IReadOnlyList<Suppression> ActiveSuppressions)
{
    public Severity Overall =>
        Findings.Count == 0 ? Severity.Green : Findings.Max(f => f.Severity);
}
