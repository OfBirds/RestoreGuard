using System.Text.Json;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Providers.Smart;

/// <summary>
/// SMART health of PHYSICAL disks, read via smartctl on the hypervisors. (TrueNAS is
/// a VM and only sees QEMU virtual disks with no SMART — the real disks live on the
/// PVE hosts, so that's where health must be read.)
/// </summary>
public sealed class SmartProvider(ISshProvider ssh)
{
    public async Task<IReadOnlyList<StorageTarget>> GetAsync(string hostAlias, CancellationToken ct = default)
    {
        // One round trip: scan, then health-check each device. smartctl exits non-zero
        // for failing disks — the trailing `true` keeps the ssh exit code clean so a
        // FAILED disk reports as a finding, not a provider error.
        var result = await ssh.RunAsync(hostAlias,
            "for d in $(smartctl --scan | awk '{print $1}'); do echo \"===DEV $d\"; smartctl -H -j $d; done; true", ct);
        if (result.ExitCode != 0)
            throw new ProviderException($"smartctl scan on {hostAlias} failed: {result.StdErr.Trim()}");

        return Parse(result.StdOut, hostAlias);
    }

    public static IReadOnlyList<StorageTarget> Parse(string output, string hostAlias)
    {
        var targets = new List<StorageTarget>();

        foreach (var section in output.Split("===DEV ", StringSplitOptions.RemoveEmptyEntries))
        {
            var newline = section.IndexOf('\n');
            if (newline < 0)
                continue;
            var device = section[..newline].Trim();
            if (!device.StartsWith("/dev/", StringComparison.Ordinal))
                continue;

            var health = "UNKNOWN";
            try
            {
                using var doc = JsonDocument.Parse(section[newline..]);
                if (doc.RootElement.TryGetProperty("smart_status", out var status)
                    && status.TryGetProperty("passed", out var passed))
                {
                    health = passed.GetBoolean() ? "PASSED" : "FAILED";
                }
            }
            catch (JsonException)
            {
                // Device with no parsable SMART output stays UNKNOWN — visible, not fatal.
            }

            targets.Add(new StorageTarget(
                Name: $"smart {device}",
                Host: hostAlias,
                CapacityBytes: 0,
                FreeBytes: 0,
                Health: health,
                LastScrubOrGc: null));
        }

        return targets;
    }
}
