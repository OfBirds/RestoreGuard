using RestoreGuard.Cli;
using RestoreGuard.Providers;

var parsed = CliArgs.Parse(args);
if (parsed.Error is not null)
{
    Console.Error.WriteLine(parsed.Error);
    Console.Error.WriteLine();
    Program.PrintUsage();
    return 2;
}

// First Ctrl+C: cancel gracefully — every in-flight ssh dies, providers degrade
// to errors, and the audit still prints a partial report saying exactly which
// probes finished and which were cut off. Second Ctrl+C: terminate hard.
var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (!shutdown.IsCancellationRequested)
    {
        e.Cancel = true;
        Console.Error.WriteLine("Ctrl+C - stopping probes, reporting what finished (press again to exit hard)...");
        shutdown.Cancel();
    }
};

var ssh = new SshProvider(shutdown.Token);

if (parsed.Command == "help")
{
    Program.PrintUsage();
    return 0;
}

if (parsed.Command == "interactive")
    return await InteractiveMode.RunAsync(parsed.ConfigPath, ssh);

if (parsed.Command == "init")
    return await InteractiveMode.RunSetupAsync(parsed.ConfigPath, ssh) ? 0 : 2;

if (!File.Exists(parsed.ConfigPath))
{
    Console.Error.WriteLine($"Config not found: {parsed.ConfigPath} (run bare `restoreguard` for guided setup, or pass -c <path>).");
    return 2;
}

var config = RestoreGuardConfig.LoadValidated(parsed.ConfigPath);
if (config is null)
    return 2;
var configDir = Path.GetDirectoryName(Path.GetFullPath(parsed.ConfigPath))!;

return parsed.Command switch
{
    "doctor" => await Doctor.RunAsync(config, ssh),
    _ => await AuditRunner.RunAsync(config, configDir, ssh, parsed.Json),
};

public partial class Program
{
    public static void PrintUsage()
    {
        Console.WriteLine("""
            RestoreGuard — backup-integrity & restore-drift auditor (read-only, over SSH).

            Usage:
              restoreguard                    interactive: guided setup on first run,
                                              then a small menu
              restoreguard audit [options]    run the audit
              restoreguard doctor [options]   preflight: verify every configured
                                              requirement, host by host
              restoreguard init [options]     redo the guided setup (existing config
                                              is backed up to <name>.bak)
              restoreguard help               show this help

            Options:
              -c, --config <path>   config file (default: ./restoreguard.json — see
                                    restoreguard.sample.json for an annotated template)
              --json                machine-readable JSON report instead of the
                                    colored console report

            Exit codes:
              0  no RED findings (doctor: all requirements satisfied)
              1  at least one RED finding, or discovery was partial (a provider failed)
              2  config problem, unknown argument, or failed preflight
            """);
    }
}
