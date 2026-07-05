using System.Diagnostics;

namespace RestoreGuard.Providers;

/// <summary>
/// Runs commands over the machine's own `ssh` binary so host aliases, keys and
/// per-machine quirks stay in ~/.ssh/config where they already live.
/// </summary>
public sealed class SshProvider : ISshProvider
{
    public async Task<SshResult> RunAsync(string hostAlias, string command, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("ssh")
        {
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

        var stdOut = process.StandardOutput.ReadToEndAsync(ct);
        var stdErr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new SshResult(process.ExitCode, await stdOut, await stdErr);
    }
}
