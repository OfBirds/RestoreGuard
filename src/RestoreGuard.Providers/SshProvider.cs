using System.Diagnostics;

namespace RestoreGuard.Providers;

/// <summary>
/// Runs commands over the machine's own `ssh` binary so host aliases, keys and
/// per-machine quirks stay in ~/.ssh/config where they already live.
/// </summary>
/// <param name="shutdownToken">App-wide cancellation (Ctrl+C). A canceled token
/// aborts every in-flight and future command immediately, as a normal failed
/// SshResult, so callers degrade to provider errors instead of hanging.</param>
public sealed class SshProvider(CancellationToken shutdownToken = default) : ISshProvider
{
    /// <summary>Hard ceiling per remote command. Every audit command is a quick
    /// read; anything slower means a wedged remote or dead connection, and a
    /// hung ssh must never freeze an audit (or a cron job) forever.</summary>
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(120);

    public async Task<SshResult> RunAsync(string hostAlias, string command, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ConnectTimeout=10");
        psi.ArgumentList.Add(hostAlias);
        psi.ArgumentList.Add(command);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ssh process.");

        // Close stdin immediately (the `ssh -n` equivalent). Without this, ssh
        // inherits the interactive console's stdin, and concurrent ssh children
        // competing for the same console input handle hang indefinitely on
        // Windows — the audit "freezes" even though every remote command would
        // finish in under a second.
        process.StandardInput.Close();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdownToken);
        linked.CancelAfter(CommandTimeout);

        var stdOut = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stdErr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            var reason = ct.IsCancellationRequested || shutdownToken.IsCancellationRequested
                ? "canceled (Ctrl+C)"
                : $"timed out after {CommandTimeout.TotalSeconds:0}s";
            return new SshResult(-1, "", $"ssh to '{hostAlias}' {reason} (command: {Truncate(command)})");
        }

        return new SshResult(process.ExitCode, await stdOut, await stdErr);
    }

    private static string Truncate(string command) =>
        command.Length <= 80 ? command : command[..77] + "...";
}
