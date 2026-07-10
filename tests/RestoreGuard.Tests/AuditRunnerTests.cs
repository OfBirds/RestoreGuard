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

// reports-env collection: with no reporting configured the audit persists to the
// DEFAULT folder, so these tests must redirect it (env var) away from the real
// per-user documents folder — and env vars are process-wide.
[Collection("reports-env")]
public class AuditRunnerTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rg-audit-test");
    private readonly string? _originalReportsDir = Environment.GetEnvironmentVariable(ReportPublisher.ReportsDirEnvVar);

    public AuditRunnerTests() => Environment.SetEnvironmentVariable(
        ReportPublisher.ReportsDirEnvVar, Path.Combine(_dir.FullName, "default-reports"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ReportPublisher.ReportsDirEnvVar, _originalReportsDir);
        _dir.Delete(recursive: true);
    }

    private static async Task<(int Exit, string StdOut, string StdErr)> RunAsync(RestoreGuardConfig config, string configDir)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var (origOut, origErr) = (Console.Out, Console.Error);
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await AuditRunner.RunAsync(config, configDir, new HalfDeadLabSsh(), jsonOutput: true);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task PartialFailure_ProgressOnStderr_CleanJsonOnStdout_ExitCode1()
    {
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null) with
        {
            DockerHosts = [new DockerHostConfig("goodhost"), new DockerHostConfig("deadhost")],
        };

        var (exit, stdout, progress) = await RunAsync(config, _dir.FullName);

        // A failed probe makes the run partial: exit 1, not a green lie.
        Assert.Equal(1, exit);

        // stdout is EXACTLY the JSON document — parseable, marked partial, naming the dead host.
        using var report = JsonDocument.Parse(stdout);
        Assert.True(report.RootElement.GetProperty("partial").GetBoolean());
        var errors = report.RootElement.GetProperty("providerErrors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(errors, e => e!.Contains("deadhost") && e.Contains("timed out"));

        // stderr narrates every probe: the header, the success, the failure.
        Assert.Contains("auditing: 2 probe(s)", progress);
        Assert.Contains("ok    [docker] goodhost", progress);
        Assert.Contains("FAIL  [docker] deadhost", progress);
        Assert.Contains("discovery done", progress);

        // Even with no reporting section, the report was persisted (default folder).
        Assert.Contains("report ok    folder", progress);
        Assert.True(File.Exists(Path.Combine(_dir.FullName, "default-reports", "latest.json")));
    }

    [Fact]
    public async Task ConfiguredFolderSink_ReceivesTheExactStdoutReport()
    {
        var reports = Path.Combine(_dir.FullName, "configured-reports");
        var config = new RestoreGuardConfig([new DockerHostConfig("goodhost")], null, null, 26,
            null, null, null, null, null, null,
            Reporting: new ReportingConfig(new FolderSinkConfig(reports, Id: "spool")));

        var (exit, stdout, progress) = await RunAsync(config, _dir.FullName);

        Assert.Equal(0, exit);
        Assert.Contains($"report ok    folder {reports}", progress);

        using var stored = JsonDocument.Parse(File.ReadAllText(Path.Combine(reports, "latest.json")));
        using var std = JsonDocument.Parse(stdout);
        // Stored copy is stamped with its own target; stdout has none...
        Assert.Equal("spool", stored.RootElement.GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Null, std.RootElement.GetProperty("target").ValueKind);
        // ...but the content hash is identical (target/hash are excluded from the hash).
        Assert.Equal(std.RootElement.GetProperty("hash").GetString(),
            stored.RootElement.GetProperty("hash").GetString());

        Assert.False(Directory.Exists(Path.Combine(_dir.FullName, "default-reports")));
    }

    [Fact]
    public async Task SinkFailure_IsLoudAndFailsTheExitCode()
    {
        // A FILE where the reports folder should be: CreateDirectory will throw.
        var blocked = Path.Combine(_dir.FullName, "not-a-folder");
        File.WriteAllText(blocked, "occupied");
        var config = new RestoreGuardConfig([new DockerHostConfig("goodhost")], null, null, 26,
            null, null, null, null, null, null,
            Reporting: new ReportingConfig(new FolderSinkConfig(blocked)));

        var (exit, stdout, progress) = await RunAsync(config, _dir.FullName);

        // The report still reached stdout intact...
        using var report = JsonDocument.Parse(stdout);
        Assert.False(report.RootElement.GetProperty("partial").GetBoolean());
        // ...but an undelivered report is a failed audit night: exit 1 + a FAIL line.
        Assert.Equal(1, exit);
        Assert.Contains($"report FAIL  folder {blocked}", progress);
    }
}
