using RestoreGuard.Providers;

namespace RestoreGuard.Tests;

/// <summary>
/// A tiny simulated lab for wizard tests AND the generated wizard transcripts
/// (docs/wizard-transcripts): hosts "nas", "pve", "truenas", "hypervisor" accept
/// SSH; everything else fails with Permission denied. Content probes behave like
/// the real commands do on a healthy lab.
/// </summary>
internal sealed class FakeLabSsh : ISshProvider
{
    private static readonly string[] GoodHosts = ["nas", "pve", "truenas", "hypervisor"];

    public List<(string Host, string Command)> Calls { get; } = [];

    public Task<SshResult> RunAsync(string hostAlias, string command, CancellationToken ct = default)
    {
        Calls.Add((hostAlias, command));

        if (!GoodHosts.Contains(hostAlias))
            return Fail($"{hostAlias}: Permission denied (publickey,password).");

        // Repo-open probes for the file-backup wizard flows.
        if (command.Contains("borg list --short"))
            return command.Contains("/backups/borg") && command.Contains(".borg-pass")
                ? Ok("")
                : Fail("passphrase supplied in BORG_PASSCOMMAND is incorrect");
        if (command.Contains("restic") && command.Contains("cat config"))
            return command.Contains("/mnt/restic-repo")
                ? Ok("")
                : Fail("Fatal: wrong password or no key found");

        // Restore-canary probes: /etc/fstab restores, anything else comes back as
        // 0 bytes with the tool's stderr (the pipeline's exit code is wc's: 0).
        if (command.Contains("dump latest"))
            return command.Contains("'/etc/fstab'")
                ? Ok("512\n")
                : Task.FromResult(new SshResult(0, "0\n", "Fatal: no matching entries found"));
        if (command.Contains("borg list --json"))
            return Ok("""{"archives":[{"name":"nas-2026-07-06"}]}""");
        if (command.Contains("borg extract --stdout"))
            return command.Contains("'etc/fstab'")
                ? Ok("256\n")
                : Task.FromResult(new SshResult(0, "0\n", "Include pattern never matched."));

        // SQLite hot-copy scans: /backups/appdata is clean, /backups/appdata-live
        // has two hot-copied databases, anything else isn't a directory.
        if (command.Contains("-name '*-wal'"))
        {
            if (command.Contains("'/backups/appdata'"))
                return Ok("");
            if (command.Contains("'/backups/appdata-live'"))
                return Ok("vaultwarden/db.sqlite3-wal\nhomeassistant/home-assistant_v2.db-wal\n");
            return Fail("find: no such file or directory");
        }

        // Off-site job probes: a sync log with run markers, and rclone capacity.
        if (command.StartsWith("tail "))
            return command.Contains("'/var/log/offsite-sync.log'")
                ? Ok(Fixtures.Read("pbs-sync-log-tail.txt"))
                : Fail("tail: cannot open: No such file or directory");
        if (command.StartsWith("rclone about"))
            return command.Contains("onedrive:")
                ? Ok("""{"total":5497558138880,"used":3848290697216,"free":1649267441664}""")
                : Fail("didn't find section in config file");

        // ZFS snapshot listings: tank/data has fresh-ish sanoid snapshots,
        // backup/pve-data is its replica with one shipped snapshot; anything
        // else is not a dataset.
        if (command.StartsWith("zfs list"))
        {
            if (command.Contains("'tank/data'"))
                return Ok("tank/data@autosnap_2026-07-05_00:00\t1751666400\ntank/data@autosnap_2026-07-06_00:00\t1751752800\n");
            if (command.Contains("'backup/pve-data'"))
                return Ok("backup/pve-data@autosnap_2026-07-06_00:00\t1751752800\n");
            return Fail($"cannot open '{command}': dataset does not exist");
        }

        // Docker-binary probe (wizard docker-path question): quoted path after
        // `command -v` — distinct from the smartctl probe, which is unquoted.
        if (command.StartsWith("command -v '"))
            return command.Contains("'docker'") || command.Contains("/usr/bin/docker")
                ? Ok("")
                : Fail("");

        // Checked before "echo ok": the smartctl capability probe contains both.
        if (command.Contains("smartctl"))
            return Ok(hostAlias switch
            {
                "hypervisor" or "pve" => "ok",
                "nas" => "no-disks",
                _ => "missing-tool",
            });

        if (command.Contains("echo ok"))
            return Ok("ok");

        if (command.Contains("find '/var/backups/db-prod'"))
            return Ok("12\n");
        if (command.Contains("find ")) // any other dump dir doesn't exist
            return Fail("");

        if (command.Contains("/nodes/'pve'/status") || command.Contains("/nodes/pve/status"))
            return Ok("");
        if (command.Contains("/status"))
            return Fail("no such node");

        if (command.Contains("storage/'pbs-store'/content") || command.Contains("storage/pbs-store/content"))
            return Ok("[]");
        if (command.Contains("/content"))
            return Fail("no such storage");

        if (command.Contains("pool.dataset.query"))
            return Ok(command.Contains("tank/private") ? """[{"id": "tank/private"}]""" : "[]");

        return Ok("");
    }

    private static Task<SshResult> Ok(string stdout) => Task.FromResult(new SshResult(0, stdout, ""));
    private static Task<SshResult> Fail(string stderr) => Task.FromResult(new SshResult(255, "", stderr));
}
