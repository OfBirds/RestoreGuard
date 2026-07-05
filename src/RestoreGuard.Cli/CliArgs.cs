namespace RestoreGuard.Cli;

/// <summary>Parsed command line. Command is one of: audit, doctor, help, interactive.</summary>
public sealed record ParsedArgs(string Command, string ConfigPath, bool Json, string? Error = null);

public static class CliArgs
{
    public static ParsedArgs Parse(string[] args)
    {
        string? command = null;
        var configPath = "restoreguard.json";
        var json = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "audit" or "doctor" or "init" when command is null:
                    command = args[i];
                    break;
                case "help" or "--help" or "-h":
                    return new ParsedArgs("help", configPath, json);
                case "-c" or "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "-c" or "--config":
                    return Fail("--config requires a path.");
                case "--json":
                    json = true;
                    break;
                default:
                    return Fail($"Unknown argument: {args[i]}");
            }
        }

        // Bare invocation → interactive (wizard or menu); flags without a verb → audit,
        // so `restoreguard --json` and `restoreguard -c lab.json` still do the obvious.
        command ??= args.Length == 0 ? "interactive" : "audit";
        return new ParsedArgs(command, configPath, json);

        ParsedArgs Fail(string message) => new("help", configPath, json, message);
    }
}
