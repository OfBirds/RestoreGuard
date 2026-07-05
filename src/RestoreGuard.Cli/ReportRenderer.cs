using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Cli;

public static class ReportRenderer
{
    public static void Render(Report report, LabInventory inventory)
    {
        Console.WriteLine($"RestoreGuard audit — {report.GeneratedAt:u}");
        Console.WriteLine($"{inventory.Services.Count} services, {inventory.Backups.Count} backup artifacts inspected.");
        Console.WriteLine();

        foreach (var finding in report.Findings
                     .OrderByDescending(f => f.Severity)
                     .ThenBy(f => f.Host).ThenBy(f => f.Service))
        {
            WriteSeverity(finding.Severity);
            Console.WriteLine($" {finding.Host}/{finding.Service}  [{finding.RuleId}]");
            Console.WriteLine($"       {finding.Evidence}");
            Console.WriteLine($"       -> {finding.SuggestedAction}");
            Console.WriteLine();
        }

        var servicesWithFindings = report.Findings.Select(f => (f.Host, f.Service)).Distinct().Count();
        var green = inventory.Services.Count - servicesWithFindings;
        Console.WriteLine($"Summary: {Count(report, Severity.Red)} RED, {Count(report, Severity.Yellow)} YELLOW findings across {servicesWithFindings} services; {green} services clean.");

        if (report.SuppressedFindings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Suppressed findings ({report.SuppressedFindings.Count}):");
            foreach (var f in report.SuppressedFindings)
                Console.WriteLine($"  - {f.Host}/{f.Service} [{f.RuleId}] {f.Evidence}");
        }

        if (report.ActiveSuppressions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Active suppressions ({report.ActiveSuppressions.Count}) — audit these periodically:");
            foreach (var s in report.ActiveSuppressions)
                Console.WriteLine($"  - {s.Host}/{s.Service} [{s.RuleId}] {s.Reason} (decided {s.DecidedOn:yyyy-MM-dd}{(s.Expires is { } e ? $", expires {e:yyyy-MM-dd}" : "")})");
        }

        Console.WriteLine();
        WriteSeverity(report.Overall);
        Console.WriteLine($" Overall: {report.Overall}");
    }

    private static int Count(Report report, Severity severity) =>
        report.Findings.Count(f => f.Severity == severity);

    private static void WriteSeverity(Severity severity)
    {
        var (color, label) = severity switch
        {
            Severity.Red => (ConsoleColor.Red, "RED "),
            Severity.Yellow => (ConsoleColor.Yellow, "YELLO"),
            _ => (ConsoleColor.Green, "GREEN"),
        };
        Console.ForegroundColor = color;
        Console.Write($"[{label}]");
        Console.ResetColor();
    }
}
