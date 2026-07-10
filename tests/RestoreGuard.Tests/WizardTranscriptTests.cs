using System.Text;
using RestoreGuard.Cli;

namespace RestoreGuard.Tests;

/// <summary>
/// A TextReader that echoes every consumed answer into the transcript as «answer»
/// (or «Enter» / «EOF»), so the generated dialogue reads like a real session:
/// the prompt, then what the user typed, then the wizard's reaction.
/// </summary>
file sealed class EchoReader(IReadOnlyList<string> answers, StringWriter transcript) : TextReader
{
    private int _i;

    public override string? ReadLine()
    {
        if (_i >= answers.Count)
        {
            transcript.WriteLine("«EOF»");
            return null;
        }
        var answer = answers[_i++];
        transcript.WriteLine(answer.Length == 0 ? "«Enter»" : $"«{answer}»");
        return answer;
    }
}

/// <summary>
/// The wizard's dialogue as REVIEWABLE GOLDEN FILES: every scenario drives the real
/// wizard against the simulated lab and the full transcript — prompts, answers,
/// probe verdicts, the resulting config, and every SSH command the wizard ran — is
/// committed under docs/wizard-transcripts/. Change a wizard question and this test
/// fails until the transcripts are regenerated, so every dialogue change shows up
/// as a reviewable file diff in the commit. Nobody replays the CLI by hand.
///
/// Regenerate:  bash scripts/update-wizard-transcripts.sh
///        (or:  RG_UPDATE_TRANSCRIPTS=1 dotnet test --filter WizardTranscript)
/// </summary>
public class WizardTranscriptTests
{
    private sealed record Scenario(string Title, string[] About, string[] Answers);

    private static readonly Dictionary<string, Scenario> Scenarios = new()
    {
        ["01-everything-correct"] = new(
            "Everything answered correctly",
            [
                "Every section configured, every kind of file-backup source added, and",
                "every live probe succeeding on the first try.",
            ],
            [
                "nas", "",                                   // docker host + default docker path
                "",                                          // docker: done
                "y", "", "", "", "y",                        // dumps: yes, default host+path, pg_dumpall, prod-only
                "y", "pve", "", "pbs-store",                 // pve: node default 'pve', storages
                "",                                          // pve: done
                "y", "truenas", "tank/private", "",          // truenas + one excluded dataset, done
                "y", "pve", "tank/data",                     // zfs: source dataset (2 snapshots)
                "y", "nas", "backup/pve-data",               // replicated to nas
                "", "",                                      // name default, hours default
                "",                                          // zfs: done
                "y", "pve", "/var/log/offsite-sync.log",     // offsite: log parsed live
                "onedrive:",                                 // remote answers rclone about
                "", "",                                      // name default, hours default
                "",                                          // offsite: done
                "r", "nas", "/mnt/restic-repo", "", "/etc/fstab", "", "",  // restic + canary
                "b", "nas", "/backups/borg", "", "/etc/fstab", "", "",     // borg + canary
                "d", "nas", "/var/backups/db-prod", "", "",  // dir
                "k", "nas", "", "",                          // kopia
                "s", "nas", "", "", "",                      // snapper (default config 'root')
                "h", "pve", "9000", "", "",                  // home assistant
                "",                                          // file backups: done
                "y", "nas", "/backups/appdata", "",          // sqlite scan: clean folder, name default
                "",                                          // sqlite: done
                "hypervisor", "",                            // smart + done
            ]),

        ["02-wrong-answers-rejected"] = new(
            "Wrong answers: every probe failing at least once",
            [
                "Each question gets a wrong answer first: failure messages, the",
                "keep-anyway escape hatch, retries, re-asks on typos/garbage, the",
                "Enter-skips-after-rejection behavior, and the guards that keep bad",
                "input out of the config are all on display.",
            ],
            [
                "badhost", "n", "nas",                       // ssh fails -> don't keep -> retry ok
                "/opt/nodocker", "n", "",                    // docker path typo -> don't keep -> Enter -> plain docker
                "",                                          // docker: done
                "yws",                                       // yes/no typo -> re-asked
                "y", "", "/nonexistent", "n",                // dumps: default host; bad path -> don't keep
                "/var/backups/db-prod",                      // corrected path
                "pgdump", "mysqldump",                       // method typo -> re-asked -> corrected
                "n",                                         // prod-only: no
                "y", "pve", "wrongnode", "n", "",            // pve: rejected node name -> Enter -> node skipped
                "pve", "",                                   // retry: dest ok, node default 'pve' ok
                "bogus-storage", "n", "",                    // unreadable storage -> don't keep -> EMPTY -> warning
                "",                                          // pve: done
                "y", "truenas",
                @"\management\system", "n", "",              // dataset: normalized, not found -> skip
                "y", "pve", "tank/typo", "n", "",            // zfs: bad dataset -> don't keep -> entry skipped
                "pve", "tank/data",                          // retry: dataset ok
                "y", "",                                     // replicated, but Enter for the replica host
                "", "",                                      // name, hours
                "",                                          // zfs: done
                "y", "pve", "/var/log/nope.log", "n", "",    // offsite: bad log -> don't keep -> job skipped
                "pve", "/var/log/offsite-sync.log",          // retry with the real log
                "badremote:", "n", "",                       // rclone about fails -> skip capacity
                "", "",                                      // name, hours
                "",                                          // offsite: done
                "b", "nas", "/backups/borg",
                "/root/.wrong-pass", "n", "",                // wrong passphrase -> don't keep -> source cancelled
                "r", "nas", "/mnt/restic-repo", "",
                "/nope.conf", "n", "",                       // canary restores 0 bytes -> don't keep -> skip canary
                "",                                          // restic source still added: name default
                "two days", "",                              // hours garbage -> re-asked -> Enter = default
                "x",                                         // unknown file-backup kind
                "",                                          // file backups: done
                "y", "nas", "/nope", "n", "",                // sqlite: bad folder -> skipped
                "nas", "/backups/appdata-live", "",          // retry: 2 hot-copy hits (warned, still added)
                "",                                          // sqlite: done
                "truenas", "n",                              // smart: smartmontools missing -> don't add
                "nas", "n",                                  // smart: no physical disks -> don't add
                "hypervisor", "",                            // smart: fine + done
            ]),

        ["03-everything-skipped"] = new(
            "Everything skipped or answered no",
            [
                "The user presses Enter / answers 'n' to every section: the wizard must",
                "refuse to write an empty config and say what to do instead.",
            ],
            [
                "",                                          // docker: skip
                "n",                                         // dumps: no
                "n",                                         // pve: no
                "n",                                         // truenas: no
                "n",                                         // zfs: no
                "n",                                         // offsite: no
                "",                                          // file backups: skip
                "n",                                         // sqlite: no
                "",                                          // smart: skip
            ]),
    };

    public static TheoryData<string> Names => [.. Scenarios.Keys];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Transcript_MatchesCommittedGoldenFile(string name)
    {
        var generated = await GenerateAsync(name, Scenarios[name]);
        var path = Path.Combine(RepoRoot(), "docs", "wizard-transcripts", $"{name}.txt");

        if (Environment.GetEnvironmentVariable("RG_UPDATE_TRANSCRIPTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, generated);
            return;
        }

        Assert.True(File.Exists(path),
            $"Missing transcript {path} — regenerate with: bash scripts/update-wizard-transcripts.sh");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), generated);
    }

    private static async Task<string> GenerateAsync(string name, Scenario scenario)
    {
        var dir = Directory.CreateTempSubdirectory("rg-transcript");
        try
        {
            var configPath = Path.Combine(dir.FullName, "restoreguard.json");
            var ssh = new FakeLabSsh();
            var dialogue = new StringWriter();
            var ok = await InteractiveMode.RunWizardAsync(configPath, ssh,
                new WizardIO(new EchoReader(scenario.Answers, dialogue), dialogue));

            var sb = new StringBuilder();
            sb.AppendLine($"=== Wizard transcript: {scenario.Title} ===");
            sb.AppendLine();
            sb.AppendLine("GENERATED FILE - DO NOT EDIT. Regenerate after any wizard change:");
            sb.AppendLine("  bash scripts/update-wizard-transcripts.sh");
            sb.AppendLine("(enforced by WizardTranscriptTests; user answers appear as «answer».)");
            sb.AppendLine();
            foreach (var line in scenario.About)
                sb.AppendLine(line);
            sb.AppendLine();
            sb.AppendLine("The simulated lab (see FakeLabSsh.cs): SSH works to nas/pve/truenas/");
            sb.AppendLine("hypervisor only; dumps live in /var/backups/db-prod (12 files); the restic");
            sb.AppendLine("repo is /mnt/restic-repo, borg is /backups/borg with /root/.borg-pass; the");
            sb.AppendLine("canary /etc/fstab restores, any other path restores 0 bytes; PVE has node");
            sb.AppendLine("'pve' with storage 'pbs-store'; dataset tank/private exists; smartctl: ok on");
            sb.AppendLine("pve/hypervisor, no disks on nas, not installed on truenas.");
            sb.AppendLine();
            sb.AppendLine("---------------------------------- dialogue ----------------------------------");
            sb.AppendLine(Sanitize(dialogue.ToString(), dir.FullName).TrimEnd('\n'));
            sb.AppendLine("-------------------------------------------------------------------------------");
            sb.AppendLine();

            sb.AppendLine($"Wizard result: {(ok ? "config written" : "refused to write a config")}");
            if (File.Exists(configPath))
            {
                sb.AppendLine();
                sb.AppendLine("=== resulting restoreguard.json ===");
                sb.AppendLine(File.ReadAllText(configPath).ReplaceLineEndings("\n").TrimEnd('\n'));
            }

            sb.AppendLine();
            sb.AppendLine("=== every SSH command the wizard ran (in order) ===");
            foreach (var ((host, command), i) in ssh.Calls.Select((c, i) => (c, i)))
                sb.AppendLine($"{i + 1,3}. [{host}] {command}");

            return sb.ToString().ReplaceLineEndings("\n");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Strips the temp directory out of the dialogue so transcripts are
    /// identical across machines and operating systems.</summary>
    internal static string Sanitize(string text, string tempDir) => text
        .Replace(tempDir + Path.DirectorySeparatorChar, "")
        .Replace(tempDir, ".")
        .ReplaceLineEndings("\n");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RestoreGuard.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate the repo root (RestoreGuard.slnx).");
    }

    // Shared with the reporting-wizard transcript below.
    internal static string LocateRepoRoot() => RepoRoot();
}

/// <summary>
/// Same golden-file discipline as the first-run wizard, for the report-destinations
/// wizard (the `r` menu entry). Its live probe is a real write to each destination;
/// here it's simulated OK so the transcript is deterministic. Pins the default
/// reports folder via the env var (otherwise the "Enter = &lt;default&gt;" prompt text
/// would differ per machine) — hence the reports-env collection.
/// </summary>
[Collection("reports-env")]
public class ReportingWizardTranscriptTests
{
    [Fact]
    public async Task Transcript_MatchesCommittedGoldenFile()
    {
        var generated = await GenerateAsync();
        var path = Path.Combine(WizardTranscriptTests.LocateRepoRoot(),
            "docs", "wizard-transcripts", "04-reporting-destinations.txt");

        if (Environment.GetEnvironmentVariable("RG_UPDATE_TRANSCRIPTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, generated);
            return;
        }

        Assert.True(File.Exists(path),
            $"Missing transcript {path} — regenerate with: bash scripts/update-wizard-transcripts.sh");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), generated);
    }

    private static async Task<string> GenerateAsync()
    {
        // Pin the default reports folder so the prompt text is machine-independent.
        var originalReportsDir = Environment.GetEnvironmentVariable(ReportPublisher.ReportsDirEnvVar);
        Environment.SetEnvironmentVariable(ReportPublisher.ReportsDirEnvVar,
            "/home/you/.local/share/restoreguard/reports");
        var dir = Directory.CreateTempSubdirectory("rg-transcript-reporting");
        try
        {
            // The reporting wizard EDITS an existing config, so seed a minimal one.
            var configPath = Path.Combine(dir.FullName, "restoreguard.json");
            File.WriteAllText(configPath, "{\n  \"dockerHosts\": [ { \"alias\": \"nas\" } ]\n}\n");

            string[] answers =
            [
                "y", "/var/lib/restoreguard/reports", "30",              // folder: on, path, keep newest 30
                "y", "http://192.168.1.10:9000", "backups", "rg-reports/", // s3: on, endpoint, bucket, prefix
                "", "n",                                                // region default; not AWS -> path-style
                "/etc/restoreguard/s3.access", "/etc/restoreguard/s3.secret", // both keys from files
                "y", "", "mongodb://192.168.1.11:27017",                // mongo: on, no file -> inline conn string
                "", "",                                                 // database + collection defaults
            ];

            var dialogue = new StringWriter();
            var probed = new List<string>();
            await ReportingWizard.ConfigureAsync(configPath,
                new WizardIO(new EchoReader(answers, dialogue), dialogue),
                sink =>
                {
                    probed.Add(sink.Description);
                    return Task.FromResult((true, "write access verified"));
                });

            var sb = new StringBuilder();
            sb.AppendLine("=== Wizard transcript: Report destinations (the `r` menu entry) ===");
            sb.AppendLine();
            sb.AppendLine("GENERATED FILE - DO NOT EDIT. Regenerate after any wizard change:");
            sb.AppendLine("  bash scripts/update-wizard-transcripts.sh");
            sb.AppendLine("(enforced by ReportingWizardTranscriptTests; user answers appear as «answer».)");
            sb.AppendLine();
            sb.AppendLine("Configures all three destinations (folder, S3, MongoDB). S3 secrets go to");
            sb.AppendLine("root-only files; the Mongo connection string is entered inline. Every live");
            sb.AppendLine("probe here is a simulated OK — the real wizard write-tests each destination");
            sb.AppendLine("(folder write, S3 put+delete, Mongo ping) before accepting it.");
            sb.AppendLine();
            sb.AppendLine("--------------------------------- dialogue ----------------------------------");
            sb.AppendLine(WizardTranscriptTests.Sanitize(dialogue.ToString(), dir.FullName).TrimEnd('\n'));
            sb.AppendLine("-------------------------------------------------------------------------------");
            sb.AppendLine();
            sb.AppendLine("=== destinations live-probed, in order ===");
            foreach (var (d, i) in probed.Select((d, i) => (d, i)))
                sb.AppendLine($"{i + 1,3}. {d}");
            sb.AppendLine();
            sb.AppendLine("=== resulting restoreguard.json ===");
            sb.AppendLine(File.ReadAllText(configPath).ReplaceLineEndings("\n").TrimEnd('\n'));

            return sb.ToString().ReplaceLineEndings("\n");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReportPublisher.ReportsDirEnvVar, originalReportsDir);
            dir.Delete(recursive: true);
        }
    }
}
