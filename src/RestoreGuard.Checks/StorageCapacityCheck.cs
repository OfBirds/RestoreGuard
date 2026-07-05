using RestoreGuard.Core;
using RestoreGuard.Core.Model;

namespace RestoreGuard.Checks;

public sealed record StorageCapacityOptions(
    double WarnFraction = 0.80,
    double CriticalFraction = 0.95,
    double MaxScrubAgeDays = 35);

/// <summary>
/// Catches the silent "destination full" failure plus storage-health basics: a backup
/// target filling up (or a pool quietly DEGRADED, or scrubs no longer running) stops
/// protecting data long before anyone notices.
/// </summary>
public sealed class StorageCapacityCheck(StorageCapacityOptions options) : ICheck
{
    public string RuleId => "storage";

    private static readonly string[] HealthyStates = ["available", "ONLINE", "PASSED"];

    public IEnumerable<Finding> Evaluate(LabInventory inventory)
    {
        foreach (var storage in inventory.Storage)
        {
            if (storage.Health == "UNKNOWN")
            {
                yield return new Finding(
                    "storage/health-unknown", Severity.Yellow, storage.Name, storage.Host,
                    $"Health of '{storage.Name}' could not be determined.",
                    "A device that reports no health data is a blind spot — check it manually once.");
                continue;
            }

            if (storage.Health == "inactive")
            {
                yield return new Finding(
                    "storage/inactive", Severity.Yellow, storage.Name, storage.Host,
                    $"Storage '{storage.Name}' is inactive.",
                    "An inactive storage silently drops out of backup rotation — verify it is intentional.");
                continue;
            }

            if (!HealthyStates.Contains(storage.Health))
            {
                yield return new Finding(
                    "storage/unhealthy", Severity.Red, storage.Name, storage.Host,
                    $"Storage '{storage.Name}' reports state '{storage.Health}'.",
                    "A degraded/faulted pool or errored scrub means redundancy (or data) is already compromised — investigate now.");
                continue;
            }

            if (storage.LastScrubOrGc is { } scrub)
            {
                var age = inventory.CapturedAt - scrub;
                if (age > TimeSpan.FromDays(options.MaxScrubAgeDays))
                {
                    yield return new Finding(
                        "storage/scrub-overdue", Severity.Yellow, storage.Name, storage.Host,
                        $"Last scrub/GC of '{storage.Name}' finished {age.TotalDays:F0} days ago (limit {options.MaxScrubAgeDays:F0}).",
                        "Scrubs are the early-warning system for bit rot — re-enable or run one.");
                }
            }

            if (storage.CapacityBytes <= 0)
                continue;

            var usedFraction = 1.0 - (double)storage.FreeBytes / storage.CapacityBytes;
            if (usedFraction >= options.CriticalFraction)
            {
                yield return new Finding(
                    "storage/capacity-critical", Severity.Red, storage.Name, storage.Host,
                    $"Storage '{storage.Name}' is {usedFraction:P0} full ({Gb(storage.FreeBytes)} GB free of {Gb(storage.CapacityBytes)} GB) — writes may already be failing.",
                    "Free space or expand now; new backups are likely being rejected.");
            }
            else if (usedFraction >= options.WarnFraction)
            {
                yield return new Finding(
                    "storage/capacity-warn", Severity.Yellow, storage.Name, storage.Host,
                    $"Storage '{storage.Name}' is {usedFraction:P0} full ({Gb(storage.FreeBytes)} GB free of {Gb(storage.CapacityBytes)} GB).",
                    "Plan cleanup or expansion before it reaches the critical threshold.");
            }
        }
    }

    private static long Gb(long bytes) => bytes / (1024 * 1024 * 1024);
}
