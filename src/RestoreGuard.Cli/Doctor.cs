using RestoreGuard.Providers;

namespace RestoreGuard.Cli;

/// <summary>One preflight probe: a cheap read-only command that succeeds iff the
/// host grants what the corresponding audit surface needs.</summary>
public sealed record DoctorProbe(string Host, string Area, string Command, string Requirement);

/// <summary>
/// `restoreguard --doctor`: verifies every prerequisite of the configured surfaces
/// before the first audit, so setup failures surface as named requirements instead
/// of mid-run provider errors.
/// </summary>
public static class Doctor
{
    public static IReadOnlyList<DoctorProbe> BuildProbes(RestoreGuardConfig config)
    {
        var probes = new List<DoctorProbe>();

        foreach (var h in config.DockerHosts)
        {
            probes.Add(new DoctorProbe(h.Alias, "docker",
                $"{h.DockerPath} version --format '{{{{.Server.Version}}}}' > /dev/null && {h.DockerPath} compose version > /dev/null",
                "SSH user can reach the Docker daemon and has the compose v2 plugin (>= 2.17 for `config --format json`)"));
        }

        foreach (var db in config.LogicalDbBackups ?? [])
        {
            probes.Add(new DoctorProbe(db.Host, "db-dumps",
                $"ls '{db.Path}' > /dev/null",
                $"dump directory exists and is readable ({db.Method})"));
        }

        foreach (var n in config.PveNodes ?? [])
        {
            probes.Add(new DoctorProbe(n.Alias, "pve",
                "pvesh get /version --output-format json > /dev/null",
                "pvesh available (run as root on the PVE node)"));
            foreach (var storageName in n.BackupStorages ?? [])
            {
                probes.Add(new DoctorProbe(n.Alias, "pve",
                    $"pvesh get /nodes/{n.Node}/storage/{storageName}/content --output-format json > /dev/null",
                    $"backup storage '{storageName}' content listable on node '{n.Node}'"));
            }
        }

        if (config.TrueNas is { } tn)
        {
            probes.Add(new DoctorProbe(tn.Alias, "truenas",
                "midclt call system.ready > /dev/null",
                "midclt available (TrueNAS admin user)"));
        }

        if (config.PbsOffsite is { } off)
        {
            probes.Add(new DoctorProbe(off.Alias, "pbs-offsite",
                $"test -r '{off.LogPath}' && rclone about {off.RcloneRemote} --json > /dev/null",
                "sync log readable and the rclone remote authenticates"));
        }

        foreach (var o in config.OffsiteJobs ?? [])
        {
            probes.Add(new DoctorProbe(o.Alias, "offsite", o.RcloneRemote is { Length: > 0 } r
                    ? $"test -r '{o.LogPath}' && rclone about {r} --json > /dev/null"
                    : $"test -r '{o.LogPath}'",
                o.RcloneRemote is { Length: > 0 }
                    ? $"sync log readable and the rclone remote authenticates ({o.Name})"
                    : $"sync log readable ({o.Name})"));
        }

        if (config.PbsMaintenance is { } pm)
        {
            probes.Add(new DoctorProbe(pm.ExecAlias, "pbs-maintenance",
                $"pct exec {pm.ContainerId} -- proxmox-backup-manager version > /dev/null",
                $"pct exec into CT{pm.ContainerId} (root on the PVE host)"));
        }

        foreach (var h in config.SmartHosts ?? [])
        {
            // Two distinct failure modes with distinct messages: tool missing vs. the
            // host having no physical disks at all (VM/container — SMART lives on the
            // hypervisor).
            probes.Add(new DoctorProbe(h, "smart",
                "command -v smartctl > /dev/null 2>&1 || { echo 'smartmontools is not installed (apt install smartmontools)' >&2; exit 1; }; "
                + "[ -n \"$(smartctl --scan 2>/dev/null)\" ] || { echo 'no SMART-capable physical disks visible here - this looks like a VM/container; point SMART at the hypervisor instead' >&2; exit 1; }",
                "smartmontools installed and physical disks visible (root)"));
        }

        foreach (var s in config.FileBackups ?? [])
        {
            probes.Add(new DoctorProbe(s.Alias, "file-backup", s.Kind switch
            {
                "restic" => $"restic -r '{s.Repo}' --password-file '{s.PasswordFile}' cat config > /dev/null",
                "borg" => $"BORG_PASSCOMMAND='cat {s.PasswordFile}' borg list --short '{s.Repo}' > /dev/null",
                "dir" => $"ls '{s.Path}' > /dev/null",
                "haos" => $"qm guest cmd {s.Vmid} ping",
                "snapper" => $"snapper --jsonout -c '{s.SnapperConfig}' list > /dev/null",
                "kopia" => "kopia repository status > /dev/null",
                _ => "false",
            }, s.Kind switch
            {
                "restic" => $"restic repo '{s.Repo}' opens with the password file ({s.Name})",
                "borg" => $"borg repo '{s.Repo}' opens with the passphrase file ({s.Name})",
                "dir" => $"archive directory readable ({s.Name})",
                "haos" => $"qemu guest agent responds in VM {s.Vmid} ({s.Name})",
                "snapper" => $"snapper config '{s.SnapperConfig}' listable ({s.Name})",
                "kopia" => $"kopia repository connected for the SSH user ({s.Name})",
                _ => $"unknown fileBackups kind '{s.Kind}' ({s.Name})",
            }));

            // The restore canary is preflighted with the SAME restore the audit runs —
            // a typo'd canaryPath should fail here, not as a RED on the first audit.
            // (zfs datasets get the same treatment below.)
            if (!string.IsNullOrWhiteSpace(s.CanaryPath) && s.Kind is "restic" or "borg")
            {
                probes.Add(new DoctorProbe(s.Alias, "restore-canary", s.Kind == "restic"
                        ? $"[ \"$(restic -r '{s.Repo}' --password-file '{s.PasswordFile}' --no-lock dump latest '{s.CanaryPath}' | wc -c)\" -gt 0 ]"
                        : $"a=$(BORG_PASSCOMMAND='cat {s.PasswordFile}' borg list --short --last 1 '{s.Repo}') && [ -n \"$a\" ] && "
                          + $"[ \"$(BORG_PASSCOMMAND='cat {s.PasswordFile}' borg extract --stdout \"{s.Repo}::$a\" '{s.CanaryPath!.TrimStart('/')}' | wc -c)\" -gt 0 ]",
                    $"canary '{s.CanaryPath}' restores from the latest snapshot ({s.Name})"));
            }
        }

        foreach (var s in config.SqliteBackupDirs ?? [])
        {
            probes.Add(new DoctorProbe(s.Alias, "sqlite",
                $"[ -d '{s.Path}' ]",
                $"backup directory '{s.Path}' exists ({s.Name})"));
        }

        foreach (var z in config.ZfsReplications ?? [])
        {
            probes.Add(new DoctorProbe(z.SourceAlias, "zfs-replication",
                $"zfs list '{z.SourceDataset}' > /dev/null",
                $"source dataset '{z.SourceDataset}' exists ({z.Name})"));
            if (z.TargetAlias is { Length: > 0 } && z.TargetDataset is { Length: > 0 })
            {
                probes.Add(new DoctorProbe(z.TargetAlias, "zfs-replication",
                    $"zfs list '{z.TargetDataset}' > /dev/null",
                    $"replica dataset '{z.TargetDataset}' exists ({z.Name})"));
            }
        }

        return probes;
    }

    public static async Task<int> RunAsync(RestoreGuardConfig config, ISshProvider ssh)
    {
        var probes = BuildProbes(config);
        if (probes.Count == 0)
        {
            Console.WriteLine("Nothing configured — every section of restoreguard.json is optional, but at least one is needed.");
            return 2;
        }

        Console.WriteLine($"RestoreGuard doctor — probing {probes.Count} requirement(s) across {probes.Select(p => p.Host).Distinct().Count()} host(s)…");
        Console.WriteLine();

        var results = await Task.WhenAll(probes.Select(async p =>
        {
            try
            {
                var r = await ssh.RunAsync(p.Host, p.Command);
                return (Probe: p, Ok: r.ExitCode == 0, Detail: r.StdErr.Trim());
            }
            catch (Exception ex)
            {
                return (Probe: p, Ok: false, Detail: ex.Message);
            }
        }));

        foreach (var (probe, ok, detail) in results)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? "[ OK ]" : "[FAIL]");
            Console.ResetColor();
            Console.WriteLine($" {probe.Host,-10} {probe.Area,-15} {probe.Requirement}");
            if (!ok && detail.Length > 0)
                Console.WriteLine($"        -> {Truncate(detail)}");
        }

        var failures = results.Where(r => !r.Ok).ToList();
        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("All requirements satisfied — run the audit.");
            return 0;
        }

        Console.WriteLine($"{failures.Count} requirement(s) missing — the '->' line under each [FAIL] says exactly what and where.");

        // Only lecture about SSH when a failure actually looks like SSH.
        var sshShaped = failures.Any(f =>
            f.Detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || f.Detail.Contains("resolve hostname", StringComparison.OrdinalIgnoreCase)
            || f.Detail.Contains("Host key", StringComparison.OrdinalIgnoreCase)
            || f.Detail.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
            || f.Detail.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        if (sshShaped)
        {
            Console.WriteLine("""
                For SSH failures:
                  - "Permission denied (publickey…)": no matching key was offered.
                    user@host destinations use your DEFAULT key; if the key for that box
                    lives elsewhere, use an ~/.ssh/config alias that pins it:
                        Host mybox
                            HostName 192.168.1.10
                            User root
                            IdentityFile ~/.ssh/my_other_key
                    (and the key's public half must be in the host's authorized_keys).
                  - Host key not yet accepted: connect once manually (ssh <dest> 'echo ok')
                    — the audit runs with BatchMode=yes and cannot answer prompts.
                """);
        }
        return 2;
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
