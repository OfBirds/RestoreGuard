using System.Text.Json;
using RestoreGuard.Cli;
using RestoreGuard.Providers;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Tests;

/// <summary>One host answers, one is broken: the console contract under partial
/// failure. Progress narration must go to stderr only (stdout is the --json
/// contract), every probe must be visibly accounted for, and a dead host must
/// surface as a provider error + exit 1 — never a hang, never silence.</summary>
file sealed class HalfDeadLabSsh : ISshProvider
{
    public Task<SshResult> RunAsync(string hostAlias, string command, CancellationToken ct = default) =>
        Task.FromResult(hostAlias == "goodhost"
            ? new SshResult(0, "[]", "")
            : new SshResult(-1, "", $"ssh to '{hostAlias}' timed out after 120s (command: {command[..Math.Min(20, command.Length)]}...)"));
}

public class AuditRunnerTests
{
    [Fact]
    public async Task PartialFailure_ProgressOnStderr_CleanJsonOnStdout_ExitCode1()
    {
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null) with
        {
            DockerHosts = [new DockerHostConfig("goodhost"), new DockerHostConfig("deadhost")],
        };

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var (origOut, origErr) = (Console.Out, Console.Error);
        int exit;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            exit = await AuditRunner.RunAsync(config, Path.GetTempPath(), new HalfDeadLabSsh(), jsonOutput: true);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }

        // A failed probe makes the run partial: exit 1, not a green lie.
        Assert.Equal(1, exit);

        // stdout is EXACTLY the JSON document — parseable, marked partial, naming the dead host.
        using var report = JsonDocument.Parse(stdout.ToString());
        Assert.True(report.RootElement.GetProperty("partial").GetBoolean());
        var errors = report.RootElement.GetProperty("providerErrors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(errors, e => e!.Contains("deadhost") && e.Contains("timed out"));

        // stderr narrates every probe: the header, the success, the failure.
        var progress = stderr.ToString();
        Assert.Contains("auditing: 2 probe(s)", progress);
        Assert.Contains("ok    [docker] goodhost", progress);
        Assert.Contains("FAIL  [docker] deadhost", progress);
        Assert.Contains("discovery done", progress);
    }
}
