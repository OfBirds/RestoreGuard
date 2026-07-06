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
