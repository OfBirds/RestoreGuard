namespace RestoreGuard.Core.Model;

/// <summary>
/// The outcome of one restore-canary probe: a configured sentinel file streamed
/// out of the LATEST snapshot of a file-backup source, counted on the host
/// (content never crosses the wire, nothing is written anywhere). Bytes is what
/// the restore produced; Detail carries the tool's stderr when there is any —
/// with a shell pipeline the restore tool's failure shows up as 0 bytes plus its
/// error text, not as an exit code.
/// </summary>
public sealed record CanaryResult(
    string SourceName,
    string Host,
    string CanaryPath,
    long Bytes,
    string? Detail);
