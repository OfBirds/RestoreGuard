using RestoreGuard.Cli;
using RestoreGuard.Providers;

namespace RestoreGuard.Tests;

/// <summary>
/// A tiny simulated lab for wizard tests: hosts "nas", "pve", "truenas" accept SSH;
/// everything else fails with Permission denied. Content probes behave like the real
/// commands do on a healthy lab.
/// </summary>
file sealed class FakeLabSsh : ISshProvider
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

public class WizardTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rg-wizard-test");
    private string ConfigPath => Path.Combine(_dir.FullName, "restoreguard.json");

    public void Dispose() => _dir.Delete(recursive: true);

    private async Task<(bool Ok, string Output)> RunWizardAsync(params string[] answers)
    {
        var output = new StringWriter();
        var ok = await InteractiveMode.RunWizardAsync(ConfigPath, new FakeLabSsh(),
            new WizardIO(new StringReader(string.Join('\n', answers)), output));
        return (ok, output.ToString());
    }

    // ---------- happy path ----------

    [Fact]
    public async Task FullHappyPath_WritesValidLoadableConfig()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "", "",                        // docker: nas, default path, done
            "y", "", "", "", "y",                 // dumps: yes, default host+path, pg_dumpall, prod-naming
            "y", "pve", "", "pbs-store", "",      // pve: node default 'pve', storage ok, done
            "y", "truenas", "tank/private", "",   // truenas + one excluded dataset
            "",                                   // file backups: skip
            "hypervisor", "");                    // smart: one good host, done

        Assert.True(ok);
        Assert.Contains("Configured: 1 Docker host(s), DB dumps, 1 Proxmox node(s), TrueNAS, SMART on 1 host(s)", output);

        var config = RestoreGuardConfig.Load(ConfigPath);
        Assert.Empty(config.Validate());
        Assert.Equal("nas", Assert.Single(config.DockerHosts).Alias);
        var dumpJob = Assert.Single(config.LogicalDbBackups!);
        Assert.Equal(("nas", "/var/backups/db-prod", "pg_dumpall", true),
            (dumpJob.Host, dumpJob.Path, dumpJob.Method, dumpJob.RequireProdNaming));
        Assert.Equal(["pbs-store"], Assert.Single(config.PveNodes!).BackupStorages);
        Assert.Equal("tank/private", Assert.Single(config.TrueNas!.ExcludeDatasets));
        Assert.Equal(["hypervisor"], config.SmartHosts);
        Assert.True(File.Exists(Path.Combine(_dir.FullName, "suppressions.json")));
    }

    [Fact]
    public async Task NothingConfigured_RefusesToWriteConfig()
    {
        var (ok, output) = await RunWizardAsync("", "n", "n", "n", "");

        Assert.False(ok);
        Assert.Contains("Nothing was configured", output);
        Assert.False(File.Exists(ConfigPath));
    }

    // ---------- SSH destination handling ----------

    [Fact]
    public async Task BadSshDestination_RetriedUntilGood()
    {
        var (ok, output) = await RunWizardAsync(
            "wronghost", "n", "nas", "", "",      // fail -> don't keep -> retry good
            "n", "n", "n", "");

        Assert.True(ok);
        Assert.Contains("Permission denied", output);
        Assert.Equal("nas", Assert.Single(RestoreGuardConfig.Load(ConfigPath).DockerHosts).Alias);
    }

    [Fact]
    public async Task BadSshDestination_KeepAnywayIsHonored()
    {
        var (ok, _) = await RunWizardAsync(
            "downhost", "y", "", "",              // fail -> keep anyway
            "n", "n", "n", "");

        Assert.True(ok);
        Assert.Equal("downhost", Assert.Single(RestoreGuardConfig.Load(ConfigPath).DockerHosts).Alias);
    }

    [Fact]
    public async Task PermissionDenied_ExplainsDefaultKeyCause()
    {
        var (_, output) = await RunWizardAsync("root@10.0.0.1", "n", "", "n", "n", "n", "");

        Assert.Contains("DEFAULT", output);
        Assert.Contains("IdentityFile", output);
    }

    // ---------- the skip-trap regression (found by live exercise 2026-07-05) ----------

    [Fact]
    public async Task FailedProbeWithDefault_EnterSkipsInsteadOfLoopingForever()
    {
        // dumps on a host without the dump dir: probe fails -> don't keep -> Enter
        // must SKIP (the default is cleared), not re-submit the default forever.
        var (ok, output) = await RunWizardAsync(
            "pve", "", "",                        // docker host so the config isn't empty
            "y", "", "/nonexistent", "n", "",     // dumps: default host, bad path, don't keep, Enter=skip
            "n", "n", "");

        Assert.True(ok);
        Assert.Contains("(press Enter to skip this question)", output);
        Assert.Contains("Skipping database dumps", output);
        Assert.Null(RestoreGuardConfig.Load(ConfigPath).LogicalDbBackups);
    }

    [Fact]
    public async Task SectionYesButEmptyHost_SkipsSectionCleanly()
    {
        var (ok, _) = await RunWizardAsync(
            "nas", "", "",
            "y", "", "", "mysqldump", "n", // dumps yes; defaults; mysqldump; no prod-naming
            "n",
            "y", "",     // truenas yes but Enter for destination -> skipped
            "");

        Assert.True(ok);
        var config = RestoreGuardConfig.Load(ConfigPath);
        Assert.NotNull(config.LogicalDbBackups);
        Assert.Null(config.TrueNas);
    }

    // ---------- content validation ----------

    [Fact]
    public async Task DatasetBackslashes_AreNormalized_AndMissingDatasetExplained()
    {
        var (ok, output) = await RunWizardAsync(
            "", "n", "n",
            "y", "truenas",
            @"\management\system", "n",           // backslash folder -> normalized -> not a dataset -> don't keep
            "tank/private", "",                   // real dataset accepted
            "");

        Assert.True(ok);
        Assert.Contains("(normalized to: management/system)", output);
        Assert.Contains("no DATASET with that exact path", output);
        Assert.Contains("never file contents", output);
        Assert.Equal("tank/private", Assert.Single(RestoreGuardConfig.Load(ConfigPath).TrueNas!.ExcludeDatasets));
    }

    [Fact]
    public async Task MissingDataset_KeepAnywayIsHonored()
    {
        var (ok, _) = await RunWizardAsync(
            "", "n", "n",
            "y", "truenas", "tank/typo", "y", "", // not found -> keep anyway
            "");

        Assert.True(ok);
        Assert.Equal("tank/typo", Assert.Single(RestoreGuardConfig.Load(ConfigPath).TrueNas!.ExcludeDatasets));
    }

    [Fact]
    public async Task PveBogusStorage_RetriedUntilReadable()
    {
        var (ok, output) = await RunWizardAsync(
            "", "n",
            "y", "pve", "", "bogus-storage", "n", "pbs-store", "",
            "n", "");

        Assert.True(ok);
        Assert.Contains("storage 'bogus-storage' not readable", output);
        Assert.Equal(["pbs-store"], Assert.Single(RestoreGuardConfig.Load(ConfigPath).PveNodes!).BackupStorages);
    }

    [Fact]
    public async Task PveWrongNodeName_RetriedUntilFound()
    {
        var (ok, output) = await RunWizardAsync(
            "", "n",
            "y", "pve", "wrongnode", "n", "pve", "pbs-store", "",
            "n", "");

        Assert.True(ok);
        Assert.Contains("no node with that name", output);
        Assert.Equal("pve", Assert.Single(RestoreGuardConfig.Load(ConfigPath).PveNodes!).Node);
    }

    [Fact]
    public async Task DumpFolder_ReportsFileCount()
    {
        var (_, output) = await RunWizardAsync(
            "nas", "", "",
            "y", "", "", "", "n",
            "n", "n", "");

        Assert.Contains("12 dump file(s) found", output);
    }

    // ---------- SMART capability ----------

    [Fact]
    public async Task SmartHostWithoutTool_IsRejectedUnlessKept()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "", "",
            "n", "n", "n",
            "",                                   // file backups: skip
            "truenas", "n",                       // ssh ok, but missing-tool -> don't add
            "hypervisor", "");                    // fine

        Assert.True(ok);
        Assert.Contains("smartmontools is not installed", output);
        Assert.Equal(["hypervisor"], RestoreGuardConfig.Load(ConfigPath).SmartHosts);
    }

    [Fact]
    public async Task SmartHostWithoutPhysicalDisks_ExplainsHypervisor()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "", "",
            "n", "n", "n",
            "",                                   // file backups: skip
            "nas", "n", "");                      // ssh ok, no disks -> don't add

        Assert.True(ok);
        Assert.Contains("no physical disks visible", output);
        Assert.Contains("hypervisor", output);
        Assert.Null(RestoreGuardConfig.Load(ConfigPath).SmartHosts);
    }

    // ---------- file-backup sources through the wizard ----------

    [Fact]
    public async Task Wizard_AddsBorgSource_ProbingTheRepo()
    {
        var (ok, output) = await RunWizardAsync(
            "", "n", "n", "n",                    // skip docker/dumps/pve/truenas
            "b", "nas", "/backups/borg", "",      // borg on nas; passfile default probes OK
            "", "",                               // name default, hours default
            "",                                   // file backups: done
            "");                                  // smart: skip

        Assert.True(ok);
        Assert.Contains("repo opens", output);
        var config = RestoreGuardConfig.Load(ConfigPath);
        Assert.Empty(config.Validate());
        var src = Assert.Single(config.FileBackups!);
        Assert.Equal(("borg", "nas", "/backups/borg", "/root/.borg-pass", 26d),
            (src.Kind, src.Alias, src.Repo, src.PasswordFile, src.MaxAgeHours));
        Assert.Equal("borg nas /backups/borg", src.Name);
    }

    [Fact]
    public async Task Wizard_BorgWrongPassphraseFile_FailsLoudAndCancels()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "", "",                        // one docker host so the config isn't empty
            "n", "n", "n",
            "b", "nas", "/backups/borg",
            "/root/.wrong-pass", "n", "",         // probe fails -> don't keep -> Enter skips -> source cancelled
            "",                                   // file backups: done
            "");

        Assert.True(ok);
        Assert.Contains("could not open the repo", output);
        Assert.Null(RestoreGuardConfig.Load(ConfigPath).FileBackups);
    }

    [Fact]
    public async Task Wizard_AddsResticAndDirSources()
    {
        var (ok, output) = await RunWizardAsync(
            "", "n", "n", "n",
            "r", "nas", "/mnt/restic-repo", "", "", "",   // restic ok with default passfile
            "d", "nas", "/var/backups/db-prod", "", "",   // dir with real folder
            "", "");

        Assert.True(ok);
        Assert.Contains("folder exists, 12 file(s)", output);
        var config = RestoreGuardConfig.Load(ConfigPath);
        Assert.Empty(config.Validate());
        Assert.Equal(2, config.FileBackups!.Count);
        Assert.Equal("restic", config.FileBackups[0].Kind);
        Assert.Equal(("dir", "/var/backups/db-prod"), (config.FileBackups[1].Kind, config.FileBackups[1].Path));
    }

    // ---------- setup / backup ----------

    [Fact]
    public async Task RunSetup_BacksUpExistingConfig()
    {
        File.WriteAllText(ConfigPath, "{\"dockerHosts\":[]}");

        var ssh = new FakeLabSsh();
        var output = new StringWriter();
        var ok = await InteractiveMode.RunSetupAsync(ConfigPath, ssh,
            new WizardIO(new StringReader(string.Join('\n', new[] { "nas", "", "", "n", "n", "n", "" })), output));

        Assert.True(ok);
        Assert.Contains("backed up to", output.ToString());
        Assert.Equal("{\"dockerHosts\":[]}", File.ReadAllText(ConfigPath + ".bak"));
        Assert.Equal("nas", Assert.Single(RestoreGuardConfig.Load(ConfigPath).DockerHosts).Alias);
    }

    // ---------- menu ----------

    private async Task<(int Exit, string Output)> RunMenuAsync(string configJson, params string[] inputs)
    {
        File.WriteAllText(ConfigPath, configJson);
        var output = new StringWriter();
        var exit = await InteractiveMode.RunAsync(ConfigPath, new FakeLabSsh(),
            new WizardIO(new StringReader(string.Join('\n', inputs)), output));
        return (exit, output.ToString());
    }

    private const string ValidConfig = """{"dockerHosts":[{"alias":"nas"}]}""";

    [Fact]
    public async Task Menu_QuitExitsZero()
    {
        var (exit, output) = await RunMenuAsync(ValidConfig, "q");
        Assert.Equal(0, exit);
        Assert.Contains("a = run the audit", output);
    }

    [Fact]
    public async Task Menu_EndOfInputExitsCleanly()
    {
        var (exit, _) = await RunMenuAsync(ValidConfig); // no input at all -> EOF
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Menu_EnterRepromptsQuietly_UnknownInputShowsMenuOnce()
    {
        var (_, output) = await RunMenuAsync(ValidConfig, "", "xyz", "q");

        Assert.Contains("Didn't recognize 'xyz'", output);
        // The full menu appears twice: once at start, once after the unknown input —
        // but NOT for the bare Enter.
        Assert.Equal(2, CountOf(output, "a = run the audit"));
    }

    [Fact]
    public async Task Menu_BrokenConfigOnAudit_PointsToSetup()
    {
        var broken = """{"dockerHosts":[{"alias":""}]}""";
        var (_, output) = await RunMenuAsync(broken, "a", "q");

        Assert.Contains("Type  s  to redo the setup", output);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
