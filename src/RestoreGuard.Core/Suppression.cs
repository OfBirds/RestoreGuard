namespace RestoreGuard.Core;

/// <summary>
/// An owner-accepted finding that must not be reported (see docs/known-exceptions.md).
/// Suppressions are first-class: the active list is always rendered in the report,
/// and a suppression whose premise no longer holds must fail loud, not silently hide.
/// </summary>
public sealed record Suppression(
    string Host,
    string Service,
    string RuleId,
    string Reason,
    DateOnly DecidedOn,
    DateOnly? Expires = null,
    string? RetriggerCondition = null)
{
    public bool Matches(Finding finding, DateOnly today) =>
        (Expires is null || today <= Expires)
        && string.Equals(Host, finding.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Service, finding.Service, StringComparison.OrdinalIgnoreCase)
        && string.Equals(RuleId, finding.RuleId, StringComparison.OrdinalIgnoreCase);
}
