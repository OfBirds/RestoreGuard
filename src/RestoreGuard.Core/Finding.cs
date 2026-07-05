namespace RestoreGuard.Core;

public enum Severity
{
    Green,
    Yellow,
    Red,
}

/// <summary>One check result. Evidence must be concrete enough to act on without re-running the audit.</summary>
public sealed record Finding(
    string RuleId,
    Severity Severity,
    string Service,
    string Host,
    string Evidence,
    string SuggestedAction);
