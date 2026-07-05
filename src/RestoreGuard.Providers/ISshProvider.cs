namespace RestoreGuard.Providers;

/// <summary>
/// Read-only SSH fallback for anything without a clean API: reading on-host compose
/// files, mountpoint/df checks, dump-file sizes, smartctl. Strictly no writes —
/// discovery must never mutate the lab.
/// </summary>
public interface ISshProvider
{
    Task<SshResult> RunAsync(string hostAlias, string command, CancellationToken ct = default);
}

public sealed record SshResult(int ExitCode, string StdOut, string StdErr);
