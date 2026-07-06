using RestoreGuard.Cli;

namespace RestoreGuard.Tests;

// The simulated lab (hosts, probe behaviors) lives in FakeLabSsh.cs — shared with
// the generated wizard transcripts in docs/wizard-transcripts.

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
            "n", "n",                             // zfs: no, offsite: no
            "",                                   // file backups: skip
            "n",                                  // sqlite: no
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

    // ---------- ask-time hardening (from the 2026-07-06 dialogue review) ----------

    [Fact]
    public async Task DockerPathTypo_Rejected_EmptyFallsBackToPlainDocker()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "/opt/nodocker", "n", "",      // bad path -> don't keep -> Enter -> plain docker
            "", "n", "n", "n", "", "");

        Assert.True(ok);
        Assert.Contains("no docker binary at '/opt/nodocker' on nas", output);
        Assert.Contains("(using plain `docker`)", output);
        Assert.Equal("docker", Assert.Single(RestoreGuardConfig.Load(ConfigPath).DockerHosts).DockerPath);
    }

    [Fact]
    public async Task DumpMethodTypo_ReAskedUntilValid()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "", "",
            "y", "", "", "pgdump", "mysqldump", "n",  // typo -> re-asked -> corrected
            "n", "n", "", "");

        Assert.True(ok);
        Assert.Contains("'pgdump' is not one of: pg_dumpall, mysqldump, mongodump", output);
        Assert.Equal("mysqldump", Assert.Single(RestoreGuardConfig.Load(ConfigPath).LogicalDbBackups!).Method);
    }

    [Fact]
    public async Task YesNoTypo_ReAsksInsteadOfSilentlyMeaningNo()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "", "",
            "yws", "n",                           // typo -> re-asked -> no
            "n", "n", "", "");

        Assert.True(ok);
        Assert.Contains("Please answer y or n (Enter = no).", output);
        Assert.Null(RestoreGuardConfig.Load(ConfigPath).LogicalDbBackups);
    }

    [Fact]
    public async Task HoursGarbage_ReAskedInsteadOfSilentDefault()
    {
        var (ok, output) = await RunWizardAsync(
            "", "n", "n", "n", "n", "n",
            "d", "nas", "/var/backups/db-prod", "",
            "two days", "",                       // garbage -> re-asked -> Enter = default
            "", "");

        Assert.True(ok);
        Assert.Contains("'two days' is not a positive number of hours", output);
        Assert.Equal(26d, Assert.Single(RestoreGuardConfig.Load(ConfigPath).FileBackups!).MaxAgeHours);
    }

    [Fact]
    public async Task PveEmptyNodeNameAfterRejection_SkipsNodeInsteadOfWritingInvalidConfig()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "", "",
            "n",
            "y", "pve", "wrongnode", "n", "",     // rejected node name -> Enter -> node skipped
            "",                                   // pve: done (no nodes)
            "n", "", "");

        Assert.True(ok);
        Assert.Contains("Skipping this node (no node name).", output);
        var config = RestoreGuardConfig.Load(ConfigPath);
        Assert.Null(config.PveNodes);
        Assert.Empty(config.Validate()); // the old behavior wrote pveNodes[0].node = ""
    }

    [Fact]
    public async Task PveEmptyStorageList_WarnsEveryGuestWillBeRed()
    {
        var (ok, output) = await RunWizardAsync(
            "", "n",
            "y", "pve", "", "",                   // node default ok; storages EMPTY
            "",
            "n", "", "");

        Assert.True(ok);
        Assert.Contains("no backup storages listed", output);
        Assert.Contains("image-backup/uncovered", output);
        Assert.Null(Assert.Single(RestoreGuardConfig.Load(ConfigPath).PveNodes!).BackupStorages);
    }

    // ---------- SMART capability ----------

    [Fact]
    public async Task SmartHostWithoutTool_IsRejectedUnlessKept()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "", "",
            "n", "n", "n", "n", "n",
            "",                                   // file backups: skip
            "n",                                  // sqlite: no
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
            "n", "n", "n", "n", "n",
            "",                                   // file backups: skip
            "n",                                  // sqlite: no
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
            "", "n", "n", "n", "n", "n",          // skip dumps/pve/truenas/zfs/offsite
            "b", "nas", "/backups/borg", "",      // borg on nas; passfile default probes OK
            "",                                   // canary: skip
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
        Assert.Null(src.CanaryPath);
    }

    [Fact]
    public async Task Wizard_BorgWrongPassphraseFile_FailsLoudAndCancels()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "", "",                        // one docker host so the config isn't empty
            "n", "n", "n", "n", "n",
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
            "", "n", "n", "n", "n", "n",
            "r", "nas", "/mnt/restic-repo", "", "", "", "",   // restic ok with default passfile, canary skipped
            "d", "nas", "/var/backups/db-prod", "", "",       // dir with real folder
            "", "");

        Assert.True(ok);
        Assert.Contains("folder exists, 12 file(s)", output);
        var config = RestoreGuardConfig.Load(ConfigPath);
        Assert.Empty(config.Validate());
        Assert.Equal(2, config.FileBackups!.Count);
        Assert.Equal("restic", config.FileBackups[0].Kind);
        Assert.Equal(("dir", "/var/backups/db-prod"), (config.FileBackups[1].Kind, config.FileBackups[1].Path));
    }

    [Fact]
    public async Task Wizard_CanaryIsProbedLive_AndWrittenToConfig()
    {
        var (ok, output) = await RunWizardAsync(
            "", "n", "n", "n", "n", "n",
            "b", "nas", "/backups/borg", "",      // borg on nas; passfile default probes OK
            "/etc/fstab",                          // canary — live-probed: extract restores 256 bytes
            "", "",                                // name, hours
            "", "");

        Assert.True(ok);
        Assert.Contains("restored 256 bytes from the latest snapshot", output);
        Assert.Contains("1 file-backup source(s) (1 with restore canary)", output);
        var src = Assert.Single(RestoreGuardConfig.Load(ConfigPath).FileBackups!);
        Assert.Equal("/etc/fstab", src.CanaryPath);
    }

    [Fact]
    public async Task Wizard_CanaryThatDoesNotRestore_FailsLoud_SkipKeepsSourceWithoutCanary()
    {
        var (ok, output) = await RunWizardAsync(
            "", "n", "n", "n", "n", "n",
            "r", "nas", "/mnt/restic-repo", "",   // restic ok with default passfile
            "/nope.conf", "n", "",                // canary restores 0 bytes -> don't keep -> Enter skips
            "", "",                               // name, hours
            "", "");

        Assert.True(ok);
        Assert.Contains("restored 0 bytes", output);
        Assert.Contains("no matching entries", output);
        var src = Assert.Single(RestoreGuardConfig.Load(ConfigPath).FileBackups!);
        Assert.Null(src.CanaryPath);
    }

    // ---------- ZFS replication through the wizard ----------

    [Fact]
    public async Task Wizard_ZfsReplicatedDataset_ProbedOnBothHosts_WrittenToConfig()
    {
        var (ok, output) = await RunWizardAsync(
            "", "n", "n", "n",
            "y",                                  // zfs: yes
            "pve", "tank/data",                   // source probed: 2 snapshots
            "y", "nas", "backup/pve-data",        // replica probed: 1 snapshot
            "", "",                               // name default, hours default
            "",                                   // zfs: done
            "", "");

        Assert.True(ok);
        Assert.Contains("dataset found, 2 snapshot(s)", output);
        Assert.Contains("dataset found, 1 snapshot(s)", output);
        Assert.Contains("1 ZFS dataset(s) (1 replicated)", output);
        var config = RestoreGuardConfig.Load(ConfigPath);
        Assert.Empty(config.Validate());
        var z = Assert.Single(config.ZfsReplications!);
        Assert.Equal(("pve", "tank/data", "nas", "backup/pve-data", "tank/data @ pve -> nas"),
            (z.SourceAlias, z.SourceDataset, z.TargetAlias, z.TargetDataset, z.Name));
        Assert.Equal((26d, 26d), (z.MaxSnapshotAgeHours, z.MaxReplicaAgeHours));
    }

    [Fact]
    public async Task Wizard_ZfsBadDataset_RejectedThenSkippedCleanly()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "", "",                        // docker host so the config isn't empty
            "n", "n", "n",
            "y", "pve", "tank/typo", "n", "",     // dataset probe fails -> don't keep -> Enter skips entry
            "",                                   // zfs: done
            "", "");

        Assert.True(ok);
        Assert.Contains("no dataset 'tank/typo' there", output);
        Assert.Contains("Skipping this dataset (no name).", output);
        var config = RestoreGuardConfig.Load(ConfigPath);
        Assert.Null(config.ZfsReplications);
        Assert.Empty(config.Validate());
    }

    // ---------- SQLite hot-copy scans through the wizard ----------

    [Fact]
    public async Task Wizard_SqliteScanFolder_ScannedLive_HitsAreAWarningNotARejection()
    {
        var (ok, output) = await RunWizardAsync(
            "", "n", "n", "n", "n", "n",
            "",                                   // file backups: skip
            "y",                                  // sqlite: yes
            "nas", "/backups/appdata-live",       // dir with 2 hot-copy hits — still valid config
            "",                                   // name default
            "",                                   // sqlite: done
            "");

        Assert.True(ok);
        Assert.Contains("2 WAL/SHM file(s) found: the first audit WILL flag this", output);
        Assert.Contains("1 SQLite scan folder(s)", output);
        var dir = Assert.Single(RestoreGuardConfig.Load(ConfigPath).SqliteBackupDirs!);
        Assert.Equal(("nas", "/backups/appdata-live"), (dir.Alias, dir.Path));
    }

    [Fact]
    public async Task Wizard_SqliteBadFolder_RejectedThenSkipped()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "", "",
            "n", "n", "n", "n", "n",
            "",                                   // file backups: skip
            "y", "nas", "/nope", "n", "",         // bad folder -> don't keep -> Enter -> skipped
            "",                                   // sqlite: done
            "");

        Assert.True(ok);
        Assert.Contains("folder not found on the host", output);
        Assert.Contains("Skipping this folder (no path).", output);
        Assert.Null(RestoreGuardConfig.Load(ConfigPath).SqliteBackupDirs);
    }

    // ---------- off-site jobs through the wizard ----------

    [Fact]
    public async Task Wizard_OffsiteJob_LogParsedLive_RemoteProbed_WrittenToConfig()
    {
        var (ok, output) = await RunWizardAsync(
            "", "n", "n", "n", "n",
            "y",                                  // offsite: yes
            "pve", "/var/log/offsite-sync.log",   // log probed: last run parsed out of it
            "onedrive:",                          // remote answers rclone about
            "", "",                               // name default, hours default
            "",                                   // offsite: done
            "", "");

        Assert.True(ok);
        Assert.Contains("log readable — last run 2026-07-04 05:00, rc=0", output);
        Assert.Contains("remote answers `rclone about`", output);
        Assert.Contains("1 off-site job(s)", output);
        var config = RestoreGuardConfig.Load(ConfigPath);
        Assert.Empty(config.Validate());
        var job = Assert.Single(config.OffsiteJobs!);
        Assert.Equal(("pve", "/var/log/offsite-sync.log", "onedrive:", "offsite pve offsite-sync"),
            (job.Alias, job.LogPath, job.RcloneRemote, job.Name));
    }

    [Fact]
    public async Task Wizard_OffsiteBadLog_RejectedThenSkipped_BadRemoteSkipsCapacity()
    {
        var (ok, output) = await RunWizardAsync(
            "nas", "", "",                        // docker host so the config isn't empty
            "n", "n", "n", "n",
            "y", "pve", "/var/log/nope.log", "n", "",  // bad log -> don't keep -> Enter -> job skipped
            "pve", "/var/log/offsite-sync.log",   // retry with the real log
            "badremote:", "n", "",                // rclone about fails -> don't keep -> Enter -> no capacity
            "", "",                               // name, hours
            "",                                   // offsite: done
            "", "");

        Assert.True(ok);
        Assert.Contains("log not readable there", output);
        Assert.Contains("Skipping this job (no log path).", output);
        Assert.Contains("`rclone about` failed", output);
        var job = Assert.Single(RestoreGuardConfig.Load(ConfigPath).OffsiteJobs!);
        Assert.Null(job.RcloneRemote);
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
