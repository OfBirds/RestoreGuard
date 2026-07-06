using System.Text.Json;
using RestoreGuard.Providers;
using RestoreGuard.Providers.Docker;
using RestoreGuard.Providers.Pve;

namespace RestoreGuard.Cli;

/// <summary>
/// What bare `restoreguard` does: a guided first-run wizard when no config exists,
/// a small menu when one does. One-shot usage stays `restoreguard audit` / `doctor`.
/// All console traffic flows through WizardIO so every interactive path is testable.
/// </summary>
public static class InteractiveMode
{
    public static async Task<int> RunAsync(string configPath, ISshProvider ssh, WizardIO? io = null)
    {
        if (io is null && Console.IsInputRedirected)
        {
            // No TTY (cron, pipes): interactive mode would hang — behave like help.
            Program.PrintUsage();
            return 2;
        }
        io ??= WizardIO.RealConsole;

        var freshFromWizard = false;
        if (!File.Exists(configPath))
        {
            if (!await RunWizardAsync(configPath, ssh, io))
                return 2;
            freshFromWizard = true;
        }

        return await MenuLoopAsync(configPath, ssh, io, freshFromWizard);
    }

    /// <summary>`restoreguard init`: redo guided setup; an existing config is backed up.</summary>
    public static async Task<bool> RunSetupAsync(string configPath, ISshProvider ssh, WizardIO? io = null)
    {
        if (io is null && Console.IsInputRedirected)
        {
            Console.Error.WriteLine("init is interactive and needs a terminal.");
            return false;
        }
        io ??= WizardIO.RealConsole;

        if (File.Exists(configPath))
        {
            var backup = configPath + ".bak";
            File.Copy(configPath, backup, overwrite: true);
            File.Delete(configPath);
            io.WriteLine($"Existing config backed up to {backup}.");
        }

        return await RunWizardAsync(configPath, ssh, io);
    }

    private static async Task<int> MenuLoopAsync(string configPath, ISshProvider ssh, WizardIO io, bool suggestDoctor)
    {
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;

        io.WriteLine();
        io.WriteLine(suggestDoctor
            ? "Config ready. Start with  d  (doctor) — it verifies each host is reachable and set up correctly."
            : $"Using {Path.GetFileName(configPath)}. Type a letter and press Enter:");
        PrintMenu(io);

        while (true)
        {
            io.WriteLine();
            io.Write("restoreguard> ");
            var choice = io.ReadLine()?.Trim().ToLowerInvariant();

            var config = choice is "a" or "audit" or "j" or "json" or "d" or "doctor"
                ? RestoreGuardConfig.LoadValidated(configPath)
                : null;
            if (choice is "a" or "audit" or "j" or "json" or "d" or "doctor" && config is null)
            {
                io.WriteLine("Type  s  to redo the setup, or  q  to quit and edit the file.");
                continue;
            }

            switch (choice)
            {
                case "a" or "audit":
                    await AuditRunner.RunAsync(config!, configDir, ssh, jsonOutput: false);
                    break;
                case "j" or "json":
                    await AuditRunner.RunAsync(config!, configDir, ssh, jsonOutput: true);
                    break;
                case "d" or "doctor":
                    await Doctor.RunAsync(config!, ssh);
                    break;
                case "s" or "setup" or "init":
                    if (await RunSetupAsync(configPath, ssh, io))
                        io.WriteLine("Setup done — run  d  (doctor) to verify the remaining per-host requirements.");
                    break;
                case "q" or "quit" or "exit" or null:
                    return 0;
                case "":
                    break; // just Enter: re-prompt quietly, don't nag
                default:
                    io.WriteLine($"Didn't recognize '{choice}'.");
                    PrintMenu(io);
                    break;
            }
        }
    }

    private static void PrintMenu(WizardIO io)
    {
        io.WriteLine("  a = run the audit       d = doctor (verify host access)");
        io.WriteLine("  j = audit, JSON output  s = setup (redo the config)");
        io.WriteLine("  q = quit");
    }

    /// <summary>Builds a first config from a few questions, live-testing every SSH
    /// destination AND every content answer (paths, storage names, datasets) on the
    /// target as it is entered. Covers the common core; advanced sections point at
    /// restoreguard.sample.json.</summary>
    public static async Task<bool> RunWizardAsync(string configPath, ISshProvider ssh, WizardIO io)
    {
        io.WriteLine($"""

            Welcome to RestoreGuard! No config found at {configPath}, so let's set one up.
            A few questions; everything is optional — press Enter to skip or accept the
            [default].

            RestoreGuard connects to your machines over SSH, read-only. Wherever a
            question asks for an "SSH destination", answer either:

              - an alias from your ~/.ssh/config (recommended — it pins the right key):
                    Host nas
                        HostName 192.168.1.10
                        User root
                        IdentityFile ~/.ssh/id_ed25519
                then answer:  nas
              - or user@host, e.g.  root@192.168.1.10  (this uses your DEFAULT ssh key)

            Every destination you enter is tested immediately, so a typo or key problem
            shows up right here, not later.
            """);
        io.WriteLine();

        io.WriteLine("--- Docker hosts (checks compose config vs. what containers actually run) ---");
        var dockerHosts = new List<DockerHostConfig>();
        while (true)
        {
            var alias = await AskSshDestinationAsync(ssh, io,
                $"Docker host #{dockerHosts.Count + 1} SSH destination (e.g. nas or root@192.168.1.10; Enter = {(dockerHosts.Count == 0 ? "skip Docker" : "done")})");
            if (alias.Length == 0)
                break;
            var dockerPath = await AskProbedAsync(io,
                "  path to docker on that host (Enter if plain `docker` works there)", "docker",
                async p =>
                {
                    var r = await ssh.RunAsync(alias, $"command -v {Sh(p)} > /dev/null 2>&1 || [ -x {Sh(p)} ]");
                    return r.ExitCode == 0
                        ? (true, "docker binary found")
                        : (false, $"no docker binary at '{p}' on {alias} — check the path (`which docker` there)");
                });
            if (dockerPath.Length == 0)
            {
                dockerPath = "docker"; // skipped after a rejection: fall back to PATH lookup
                io.WriteLine("  (using plain `docker`)");
            }
            dockerHosts.Add(new DockerHostConfig(alias, dockerPath));
        }

        io.WriteLine();
        io.WriteLine("--- Database dumps (a folder of nightly <name>_<yyyyMMdd>.sql.gz files) ---");
        LogicalDbBackupConfig? logicalDb = null;
        if (AskYesNo(io, "Do you have a dump folder like that to watch?"))
        {
            var host = await AskSshDestinationAsync(ssh, io,
                "  SSH destination of the machine holding the dumps (e.g. nas or root@192.168.1.10; Enter = skip)",
                dockerHosts.FirstOrDefault()?.Alias ?? "");
            if (host.Length == 0)
            {
                io.WriteLine("  Skipping database dumps for now.");
            }
            else
            {
                var path = await AskProbedAsync(io, "  folder with the dump files", "/var/backups/db-prod",
                    async p =>
                    {
                        var r = await ssh.RunAsync(host, $"find {Sh(p)} -maxdepth 1 -name '*.sql.gz' 2>/dev/null | wc -l; [ -d {Sh(p)} ]");
                        if (r.ExitCode != 0)
                            return (false, "folder not found on the host — check the path");
                        var count = r.StdOut.Trim();
                        return (true, count == "0"
                            ? "folder exists (no .sql.gz files yet — fine if the job hasn't run)"
                            : $"folder exists, {count} dump file(s) found");
                    });
                if (path.Length > 0)
                {
                    // Validated at ask time: a typo'd method would otherwise be accepted
                    // silently and only explode at config load.
                    string method;
                    while (true)
                    {
                        method = Ask(io, "  which tool writes these dumps (pg_dumpall, mysqldump, or mongodump)", "pg_dumpall");
                        if (method is "pg_dumpall" or "mysqldump" or "mongodump")
                            break;
                        io.WriteLine($"  PROBLEM — '{method}' is not one of: pg_dumpall, mysqldump, mongodump.");
                    }
                    var prodOnly = AskYesNo(io, "  does this job dump ONLY containers named 'prod' (skipping 'staging')?");
                    logicalDb = new LogicalDbBackupConfig(host, path, [host],
                        Method: method, RequireProdNaming: prodOnly);
                }
                else
                {
                    io.WriteLine("  Skipping database dumps for now.");
                }
            }
        }

        io.WriteLine();
        io.WriteLine("--- Proxmox VE (checks every VM/LXC has a fresh PBS or vzdump backup) ---");
        List<PveNodeConfig>? pveNodes = null;
        if (AskYesNo(io, "Do you run Proxmox VE?"))
        {
            pveNodes = [];
            while (true)
            {
                var alias = await AskSshDestinationAsync(ssh, io,
                    $"PVE node #{pveNodes.Count + 1} SSH destination (e.g. pve or root@192.168.1.5; Enter = done)");
                if (alias.Length == 0)
                    break;
                var node = await AskProbedAsync(io, "  the node's name in the Proxmox UI (top of the left sidebar)", "pve",
                    async n =>
                    {
                        var r = await ssh.RunAsync(alias, $"pvesh get /nodes/{Sh(n)}/status --output-format json > /dev/null");
                        return r.ExitCode == 0 ? (true, "node found") : (false, "no node with that name — check the Proxmox UI sidebar");
                    });
                if (node.Length == 0)
                {
                    // Enter after a rejected node name: without this guard the wizard
                    // would write a config its own validation rejects.
                    io.WriteLine("  Skipping this node (no node name).");
                    continue;
                }
                var storages = await AskProbedAsync(io, "  backup storage names to read, comma-separated (as listed under Datacenter > Storage; e.g. pbs-store, local)", "",
                    async s =>
                    {
                        foreach (var name in s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        {
                            var r = await ssh.RunAsync(alias, $"pvesh get /nodes/{Sh(node)}/storage/{Sh(name)}/content --output-format json > /dev/null");
                            if (r.ExitCode != 0)
                                return (false, $"storage '{name}' not readable on node '{node}' — check the name under Datacenter > Storage");
                        }
                        return (true, "all storages readable");
                    });
                if (storages.Length == 0)
                {
                    io.WriteLine("  NOTE: no backup storages listed — every guest on this node will show RED");
                    io.WriteLine("  (image-backup/uncovered) until storages to read backups from are added.");
                }
                pveNodes.Add(new PveNodeConfig(alias, node,
                    storages.Length == 0 ? null : storages.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)));
            }
            if (pveNodes.Count == 0)
                pveNodes = null;
        }

        io.WriteLine();
        io.WriteLine("--- TrueNAS SCALE (pool health, snapshots, cloud-sync tasks) ---");
        TrueNasCliConfig? trueNas = null;
        if (AskYesNo(io, "Do you run TrueNAS SCALE?"))
        {
            var alias = await AskSshDestinationAsync(ssh, io,
                "  TrueNAS SSH destination (e.g. truenas or admin@192.168.1.20; Enter = skip)");
            if (alias.Length == 0)
            {
                io.WriteLine("  Skipping TrueNAS for now.");
            }
            else
            {
                // Excluded datasets are a privacy boundary — a typo here means the
                // data you wanted protected gets scanned. Verify each one exists.
                var excluded = new List<string>();
                while (true)
                {
                    var ds = await AskProbedAsync(io,
                        $"  dataset to exclude #{excluded.Count + 1}, as pool/dataset (e.g. tank/private; Enter = {(excluded.Count == 0 ? "none" : "done")})", "",
                        async d =>
                        {
                            var r = await ssh.RunAsync(alias,
                                $"midclt call pool.dataset.query '[[\"id\",\"=\",\"{d}\"]]' '{{\"select\":[\"id\"]}}'");
                            return r.ExitCode == 0 && r.StdOut.Contains($"\"{d}\"")
                                ? (true, "found — it will be excluded before anything reads it")
                                : (false, "no DATASET with that exact path (format: pool/dataset, forward slashes). "
                                    + "If this is a folder inside a dataset: no exclusion needed — RestoreGuard only reads dataset metadata (sizes, snapshots), never file contents");
                        },
                        normalize: d => d.Replace('\\', '/').Trim('/'));
                    if (ds.Length == 0)
                        break;
                    excluded.Add(ds);
                }
                trueNas = new TrueNasCliConfig(alias, excluded);
            }
        }

        io.WriteLine();
        io.WriteLine("--- ZFS snapshots & replication (sanoid/syncoid or zfs send, outside TrueNAS) ---");
        var zfsReplications = new List<Providers.Zfs.ZfsReplicationConfig>();
        if (AskYesNo(io, "Do you snapshot/replicate ZFS datasets that RestoreGuard should watch?"))
        {
            while (true)
            {
                var sourceAlias = await AskSshDestinationAsync(ssh, io,
                    $"Dataset #{zfsReplications.Count + 1}: SSH destination of the machine holding it (Enter = {(zfsReplications.Count == 0 ? "skip" : "done")})");
                if (sourceAlias.Length == 0)
                    break;
                var sourceDataset = await AskProbedAsync(io, "  dataset (pool/dataset, as in `zfs list`)", "",
                    d => ProbeDatasetAsync(ssh, sourceAlias, d));
                if (sourceDataset.Length == 0)
                {
                    io.WriteLine("  Skipping this dataset (no name).");
                    continue;
                }

                string? targetAlias = null, targetDataset = null;
                if (AskYesNo(io, "  is it replicated somewhere (syncoid / zfs send)?"))
                {
                    targetAlias = await AskSshDestinationAsync(ssh, io,
                        "  SSH destination of the machine holding the REPLICA (Enter = skip the replica)");
                    if (targetAlias.Length > 0)
                    {
                        targetDataset = await AskProbedAsync(io, "  replica dataset (pool/dataset on that machine)", "",
                            d => ProbeDatasetAsync(ssh, targetAlias, d));
                        if (targetDataset.Length == 0)
                        {
                            io.WriteLine("  Skipping the replica (no dataset) — watching source snapshots only.");
                            targetAlias = null;
                        }
                    }
                    else
                    {
                        io.WriteLine("  Watching source snapshots only.");
                    }
                }

                var name = Ask(io, "  a name for this entry",
                    $"{sourceDataset} @ {sourceAlias}" + (targetAlias is null ? "" : $" -> {targetAlias}"));
                var hours = AskHours(io, 26); // one answer drives both limits; split via the sample json
                zfsReplications.Add(new(name, sourceAlias, sourceDataset, targetAlias, targetDataset,
                    MaxSnapshotAgeHours: hours, MaxReplicaAgeHours: hours));
            }
        }

        io.WriteLine();
        io.WriteLine("--- File-level backups (restic, borg, folders of archives, Home Assistant) ---");
        var fileBackups = new List<Providers.FileBackups.FileBackupSource>();
        while (true)
        {
            io.Write($"Add file-backup source #{fileBackups.Count + 1}?  r = restic  b = borg  k = kopia  s = btrfs snapper  d = folder of archive files  h = Home Assistant  Enter = {(fileBackups.Count == 0 ? "skip" : "done")}: ");
            var kind = io.ReadLine()?.Trim().ToLowerInvariant() ?? "";
            if (kind.Length == 0)
                break;

            switch (kind)
            {
                case "r" or "restic" or "b" or "borg":
                {
                    var isBorg = kind is "b" or "borg";
                    var tool = isBorg ? "borg" : "restic";
                    var host = await AskSshDestinationAsync(ssh, io,
                        $"  SSH destination of the machine that can open the {tool} repo (e.g. nas or root@192.168.1.10; Enter = cancel)");
                    if (host.Length == 0)
                        break;
                    var repo = Ask(io, $"  {tool} repository (a path like /mnt/nas/{tool}-repo; {(isBorg
                        ? "borg also takes ssh://user@host/path"
                        : "restic also takes s3:/sftp: URLs")})", "");
                    if (repo.Length == 0)
                        break;
                    var passFile = await AskProbedAsync(io,
                        $"  file on that host holding the repo {(isBorg ? "passphrase" : "password")}",
                        isBorg ? "/root/.borg-pass" : "/root/.restic-pass",
                        async pf =>
                        {
                            var r = await ssh.RunAsync(host, isBorg
                                ? $"BORG_PASSCOMMAND='cat {pf}' borg list --short '{repo}' > /dev/null"
                                : $"restic -r '{repo}' --password-file '{pf}' cat config > /dev/null");
                            return r.ExitCode == 0
                                ? (true, "repo opens")
                                : (false, $"could not open the repo with that file — check the repo path and the {(isBorg ? "passphrase" : "password")} file");
                        });
                    if (passFile.Length == 0)
                        break;
                    // The restore canary is probed LIVE: a real dump/extract of the file
                    // from the latest snapshot, byte-counted on the host — the same
                    // command every audit will run.
                    io.WriteLine("  Optional restore canary: every audit restores one small file from the latest");
                    io.WriteLine("  snapshot as proof the repo actually restores (read-only, counted on the host).");
                    var canary = await AskProbedAsync(io,
                        "  canary file that is ALWAYS in this backup (e.g. /etc/fstab; Enter = skip)",
                        "",
                        async c =>
                        {
                            try
                            {
                                var probe = await new Providers.FileBackups.FileBackupProvider(ssh).ProbeCanaryAsync(
                                    new("wizard probe", tool, host, Repo: repo, PasswordFile: passFile, CanaryPath: c));
                                return probe.Bytes > 0
                                    ? (true, $"restored {probe.Bytes} bytes from the latest snapshot")
                                    : (false, $"restored 0 bytes — {probe.Detail ?? "is the file really in the latest snapshot? (borg: give the path as stored, the leading / is handled)"}");
                            }
                            catch (Providers.Docker.ProviderException ex)
                            {
                                return (false, ex.Message);
                            }
                        });
                    fileBackups.Add(new(
                        Ask(io, "  a name for this source", $"{tool} {host} {repo}"),
                        tool, host, Repo: repo, PasswordFile: passFile,
                        MaxAgeHours: AskHours(io, 26),
                        CanaryPath: canary.Length > 0 ? canary : null));
                    break;
                }
                case "d" or "dir" or "folder":
                {
                    var host = await AskSshDestinationAsync(ssh, io,
                        "  SSH destination of the machine holding the archive folder (Enter = cancel)");
                    if (host.Length == 0)
                        break;
                    var path = await AskProbedAsync(io, "  folder containing the archive files", "",
                        async p =>
                        {
                            var r = await ssh.RunAsync(host, $"find {Sh(p)} -maxdepth 1 -type f 2>/dev/null | wc -l; [ -d {Sh(p)} ]");
                            return r.ExitCode == 0
                                ? (true, $"folder exists, {r.StdOut.Trim()} file(s)")
                                : (false, "folder not found on the host — check the path");
                        });
                    if (path.Length == 0)
                        break;
                    fileBackups.Add(new(
                        Ask(io, "  a name for this source", $"archives {host} {path}"),
                        "dir", host, Path: path, MaxAgeHours: AskHours(io, 26)));
                    break;
                }
                case "k" or "kopia":
                {
                    var host = await AskSshDestinationAsync(ssh, io,
                        "  SSH destination of the machine where kopia is connected to its repo (Enter = cancel)");
                    if (host.Length == 0)
                        break;
                    io.Write("  checking kopia repository status ... ");
                    var probe = await ssh.RunAsync(host, "kopia repository status > /dev/null");
                    if (probe.ExitCode != 0)
                    {
                        io.WriteLine("PROBLEM — kopia isn't connected to a repository for this SSH user");
                        io.WriteLine("  (run `kopia repository connect ...` on the host once, then re-add).");
                        if (!AskYesNo(io, "  Add it anyway?"))
                            break;
                    }
                    else
                    {
                        io.WriteLine("OK");
                    }
                    fileBackups.Add(new(
                        Ask(io, "  a name for this source", $"kopia {host}"),
                        "kopia", host, MaxAgeHours: AskHours(io, 26)));
                    break;
                }
                case "s" or "snapper":
                {
                    var host = await AskSshDestinationAsync(ssh, io,
                        "  SSH destination of the machine with the btrfs filesystem (Enter = cancel)");
                    if (host.Length == 0)
                        break;
                    var cfg = await AskProbedAsync(io, "  snapper config name (see `snapper list-configs` on that host)", "root",
                        async c =>
                        {
                            var r = await ssh.RunAsync(host, $"snapper --jsonout -c '{c}' list > /dev/null");
                            return r.ExitCode == 0
                                ? (true, "config found")
                                : (false, "no snapper config with that name — check `snapper list-configs`");
                        });
                    if (cfg.Length == 0)
                        break;
                    fileBackups.Add(new(
                        Ask(io, "  a name for this source", $"snapper {host} {cfg}"),
                        "snapper", host, SnapperConfig: cfg, MaxAgeHours: AskHours(io, 26)));
                    break;
                }
                case "h" or "haos" or "home-assistant":
                {
                    var host = await AskSshDestinationAsync(ssh, io,
                        "  SSH destination of the Proxmox host running the Home Assistant VM (Enter = cancel)");
                    if (host.Length == 0)
                        break;
                    var vmid = await AskProbedAsync(io, "  the HA VM's id in Proxmox", "",
                        async v =>
                        {
                            if (!int.TryParse(v, out _))
                                return (false, "the VM id is a number (shown next to the VM in the Proxmox sidebar)");
                            var r = await ssh.RunAsync(host, $"qm guest cmd {v} ping");
                            return r.ExitCode == 0
                                ? (true, "qemu guest agent responds")
                                : (false, "no guest-agent response — is the VM running with the agent enabled?");
                        });
                    if (vmid.Length == 0)
                        break;
                    fileBackups.Add(new(
                        Ask(io, "  a name for this source", "home-assistant full backup"),
                        "haos", host, Vmid: int.Parse(vmid),
                        MaxAgeHours: AskHours(io, 192)));
                    break;
                }
                default:
                    io.WriteLine("  r = restic, b = borg, k = kopia, s = btrfs snapper, d = folder of archives, h = Home Assistant, Enter = done.");
                    break;
            }
        }

        io.WriteLine();
        io.WriteLine("--- SMART disk health (machines with PHYSICAL disks: hypervisors/bare metal; a NAS VM only sees virtual disks) ---");
        var smartHosts = new List<string>();
        while (true)
        {
            var host = await AskSshDestinationAsync(ssh, io,
                $"SMART host #{smartHosts.Count + 1} SSH destination (e.g. pve or root@192.168.1.5; Enter = {(smartHosts.Count == 0 ? "skip SMART" : "done")})");
            if (host.Length == 0)
                break;

            // SSH working isn't enough here: the host also needs smartctl AND real disks.
            io.Write("  checking for smartctl and physical disks ... ");
            var probe = await ssh.RunAsync(host,
                "command -v smartctl > /dev/null 2>&1 || { echo missing-tool; exit 0; }; [ -n \"$(smartctl --scan 2>/dev/null)\" ] && echo ok || echo no-disks");
            var verdict = probe.StdOut.Trim();
            switch (verdict)
            {
                case "ok":
                    io.WriteLine("OK");
                    smartHosts.Add(host);
                    break;
                case "missing-tool":
                    io.WriteLine("smartmontools is not installed there (apt install smartmontools).");
                    if (AskYesNo(io, "  Add it anyway (you'll install the tool later)?"))
                        smartHosts.Add(host);
                    break;
                default:
                    io.WriteLine("no physical disks visible — this looks like a VM/container.");
                    io.WriteLine("  SMART data lives on the machine that owns the disks (the hypervisor/bare metal).");
                    if (AskYesNo(io, "  Add it anyway?"))
                        smartHosts.Add(host);
                    break;
            }
        }

        var configured = new List<string>();
        if (dockerHosts.Count > 0) configured.Add($"{dockerHosts.Count} Docker host(s)");
        if (logicalDb is not null) configured.Add("DB dumps");
        if (pveNodes is not null) configured.Add($"{pveNodes.Count} Proxmox node(s)");
        if (trueNas is not null) configured.Add("TrueNAS");
        if (fileBackups.Count > 0)
        {
            var canaries = fileBackups.Count(s => s.CanaryPath is not null);
            configured.Add($"{fileBackups.Count} file-backup source(s)"
                + (canaries > 0 ? $" ({canaries} with restore canary)" : ""));
        }
        if (zfsReplications.Count > 0)
        {
            var replicated = zfsReplications.Count(z => z.TargetDataset is not null);
            configured.Add($"{zfsReplications.Count} ZFS dataset(s)"
                + (replicated > 0 ? $" ({replicated} replicated)" : ""));
        }
        if (smartHosts.Count > 0) configured.Add($"SMART on {smartHosts.Count} host(s)");

        if (configured.Count == 0)
        {
            io.WriteLine();
            io.WriteLine("Nothing was configured, so there is nothing to audit yet.");
            io.WriteLine("Re-run `restoreguard` to try again, or copy restoreguard.sample.json and edit it by hand.");
            return false;
        }

        var config = new RestoreGuardConfig(
            dockerHosts, logicalDb is null ? null : [logicalDb], pveNodes, 26, trueNas,
            PbsOffsite: null, PbsMaintenance: null,
            SmartHosts: smartHosts.Count > 0 ? smartHosts : null,
            FileBackups: fileBackups.Count > 0 ? fileBackups : null,
            SuppressionsFile: "suppressions.json",
            ZfsReplications: zfsReplications.Count > 0 ? zfsReplications : null);

        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var suppressionsPath = Path.Combine(configDir, "suppressions.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, WizardJson));
        if (!File.Exists(suppressionsPath))
            File.WriteAllText(suppressionsPath, "[]\n");

        io.WriteLine($"""

            Configured: {string.Join(", ", configured)}.
            Wrote {configPath} (+ suppressions.json for known exceptions later).

            More to add later? Off-site sync checks and PBS GC/verify hygiene are
            documented with examples in restoreguard.sample.json.
            """);
        return true;
    }

    private static readonly JsonSerializerOptions WizardJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // The config is a local file the user reads and edits — keep "->" in a
        // name readable instead of escaping it to a \u sequence.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Asks for an SSH destination and immediately proves it works; on
    /// failure shows the cause and re-asks (with a keep-anyway escape hatch).</summary>
    private static async Task<string> AskSshDestinationAsync(ISshProvider ssh, WizardIO io, string prompt, string defaultValue = "")
    {
        while (true)
        {
            var dest = Ask(io, prompt, defaultValue);
            if (dest.Length == 0)
                return dest;

            io.Write($"  testing  ssh {dest} 'echo ok'  ... ");
            var result = await ssh.RunAsync(dest, "echo ok");
            if (result.ExitCode == 0)
            {
                io.WriteLine("OK");
                return dest;
            }

            io.WriteLine("FAILED");
            var reason = result.StdErr.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault() ?? "no error output";
            io.WriteLine($"    {reason}");
            if (reason.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            {
                io.WriteLine("    Likely cause: the wrong key was offered. user@host uses your DEFAULT");
                io.WriteLine("    ssh key — if this box needs a different key, answer with an");
                io.WriteLine("    ~/.ssh/config alias whose block sets IdentityFile for it.");
            }
            else if (reason.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
            {
                io.WriteLine($"    Connect once manually to accept the host key:  ssh {dest} 'echo ok'");
            }

            if (AskYesNo(io, "  Keep this destination anyway?"))
                return dest;

            // Re-ask with the default cleared, so Enter now means "skip" instead of
            // silently re-submitting the same failing default forever.
            if (defaultValue.Length > 0)
            {
                defaultValue = "";
                io.WriteLine("  (press Enter to skip this question)");
            }
        }
    }

    /// <summary>Asks for a value and probes it on the target; on failure shows why and
    /// re-asks, with a keep-anyway escape. Empty answers pass through unprobed.</summary>
    private static async Task<string> AskProbedAsync(
        WizardIO io, string prompt, string defaultValue,
        Func<string, Task<(bool Ok, string Message)>> probe,
        Func<string, string>? normalize = null)
    {
        while (true)
        {
            var value = Ask(io, prompt, defaultValue);
            if (value.Length == 0)
                return value;
            if (normalize is not null && normalize(value) != value)
            {
                value = normalize(value);
                io.WriteLine($"    (normalized to: {value})");
            }

            io.Write("  checking ... ");
            var (ok, message) = await probe(value);
            io.WriteLine(ok ? $"OK — {message}" : $"PROBLEM — {message}");
            if (ok)
                return value;
            if (AskYesNo(io, "  Keep this value anyway?"))
                return value;

            // Same skip-trap fix as SSH destinations: after a rejection, Enter skips.
            if (defaultValue.Length > 0)
            {
                defaultValue = "";
                io.WriteLine("  (press Enter to skip this question)");
            }
        }
    }

    /// <summary>Probes a ZFS dataset with the SAME listing the audit runs, and turns
    /// the snapshot count into the wizard's feedback (0 snapshots is not an error —
    /// the job may simply not have run yet; the audit will judge freshness).</summary>
    private static async Task<(bool Ok, string Message)> ProbeDatasetAsync(ISshProvider ssh, string alias, string dataset)
    {
        var r = await ssh.RunAsync(alias, Providers.Zfs.ZfsProvider.ListCommand(dataset));
        if (r.ExitCode != 0)
            return (false, $"no dataset '{dataset}' there (format: pool/dataset, as `zfs list` shows it)");
        var count = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return (true, count == 0
            ? "dataset found (no snapshots yet — fine if the job hasn't run)"
            : $"dataset found, {count} snapshot(s)");
    }

    private static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static double AskHours(WizardIO io, double defaultHours)
    {
        // Re-asks on garbage instead of silently using the default — the only
        // acceptable silent path is Enter, which IS the default.
        while (true)
        {
            var answer = Ask(io, "  alert when the newest backup is older than (hours)", defaultHours.ToString());
            if (double.TryParse(answer, out var h) && h > 0)
                return h;
            io.WriteLine($"  PROBLEM — '{answer}' is not a positive number of hours (Enter = {defaultHours}).");
        }
    }

    private static string Ask(WizardIO io, string prompt, string defaultValue)
    {
        io.Write(defaultValue.Length > 0 ? $"{prompt} [{defaultValue}]: " : $"{prompt}: ");
        var answer = io.ReadLine()?.Trim() ?? "";
        return answer.Length > 0 ? answer : defaultValue;
    }

    private static bool AskYesNo(WizardIO io, string prompt)
    {
        // A typo must not silently count as "no" — a mistyped yes would skip a
        // whole section without a word. Only Enter/n/no mean no; EOF means no.
        while (true)
        {
            io.Write($"{prompt} [y/N]: ");
            switch (io.ReadLine()?.Trim().ToLowerInvariant())
            {
                case "y" or "yes":
                    return true;
                case "" or "n" or "no" or null:
                    return false;
                default:
                    io.WriteLine("  Please answer y or n (Enter = no).");
                    break;
            }
        }
    }
}
