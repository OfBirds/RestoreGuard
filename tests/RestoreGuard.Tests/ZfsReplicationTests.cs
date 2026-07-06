using RestoreGuard.Checks;
using RestoreGuard.Cli;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers;
using RestoreGuard.Providers.Zfs;

namespace RestoreGuard.Tests;

file sealed class FakeZfsSsh(Func<string, string, SshResult> respond) : ISshProvider
{
    public List<(string Host, string Command)> Calls { get; } = [];

    public Task<SshResult> RunAsync(string hostAlias, string command, CancellationToken ct = default)
    {
        Calls.Add((hostAlias, command));
        return Task.FromResult(respond(hostAlias, command));
    }
}

public class ZfsReplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private static LabInventory Inventory() => new(Now, [], [], []);

    private static ZfsReplicationExpectation Expect(string name) =>
        new(name, TimeSpan.FromHours(26), TimeSpan.FromHours(26));

    // ---------- parser ----------

    [Fact]
    public void GoldenFile_NewestSnapshotIsTheLastLine()
    {
        var (name, time) = ZfsProvider.ParseNewestSnapshot(Fixtures.Read("zfs-snapshot-list.tsv"));

        // `-s creation` sorts oldest→newest: the newest wins even though a
        // syncoid snapshot sits between the sanoid dailies.
        Assert.Equal("tank/data@autosnap_2026-07-05_00:00:05_daily", name);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1783209605), time);
    }

    [Fact]
    public void EmptyListing_MeansDatasetHasNoSnapshots()
    {
        Assert.Equal((null, null), ZfsProvider.ParseNewestSnapshot(""));
    }

    // ---------- provider ----------

    [Fact]
    public async Task SourceAndTarget_AreEachListedOnTheirOwnHost()
    {
        var ssh = new FakeZfsSsh((host, _) => new SshResult(0,
            host == "pve"
                ? "tank/data@snap\t1783209605\n"
                : "backup/pve-data@snap\t1783209000\n", ""));

        var state = await new ZfsProvider(ssh).GetAsync(
            new ZfsReplicationConfig("data", "pve", "tank/data", "nas", "backup/pve-data"));

        Assert.Equal(2, ssh.Calls.Count);
        Assert.Equal(("pve", "nas"), (ssh.Calls[0].Host, ssh.Calls[1].Host));
        // -d 1 keeps the listing to the dataset's own snapshots; -H -p keeps it parseable.
        Assert.All(ssh.Calls, c => Assert.Contains("zfs list -H -p -t snapshot", c.Command));
        Assert.Contains("-d 1 'tank/data'", ssh.Calls[0].Command);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1783209605), state.NewestSourceSnapshot);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1783209000), state.NewestTargetSnapshot);
    }

    [Fact]
    public async Task SnapshotOnlyEntry_NeverTouchesASecondHost()
    {
        var ssh = new FakeZfsSsh((_, _) => new SshResult(0, "", ""));

        var state = await new ZfsProvider(ssh).GetAsync(new ZfsReplicationConfig("d", "pve", "tank/data"));

        Assert.Single(ssh.Calls);
        Assert.Null(state.NewestTargetSnapshot);
    }

    // ---------- check ----------

    private static ZfsReplicationState State(
        string name = "data",
        DateTimeOffset? source = null, DateTimeOffset? target = null,
        string? targetDataset = "backup/pve-data") =>
        new(name, "pve", "tank/data", targetDataset is null ? null : "nas", targetDataset,
            source, source is null ? null : "tank/data@newest",
            target, target is null ? null : $"{targetDataset}@newest");

    [Fact]
    public void FreshSourceAndReplica_NoFindings()
    {
        var check = new ZfsReplicationCheck(
            [State(source: Now.AddHours(-2), target: Now.AddHours(-3))], [Expect("data")]);

        Assert.Empty(check.Evaluate(Inventory()));
    }

    [Fact]
    public void DeadSnapshotJob_AndDeadReplication_AreBothRed()
    {
        var check = new ZfsReplicationCheck(
            [State(source: Now.AddDays(-3), target: Now.AddDays(-10))], [Expect("data")]);

        var findings = check.Evaluate(Inventory()).ToList();

        Assert.Equal(2, findings.Count);
        Assert.Equal("zfs-replication/snapshot-stale", findings[0].RuleId);
        Assert.Equal("zfs-replication/replica-stale", findings[1].RuleId);
        Assert.All(findings, f => Assert.Equal(Severity.Red, f.Severity));
        Assert.Contains("replication stopped while the source kept going", findings[1].Evidence);
    }

    [Fact]
    public void NoSnapshotsAtAll_AndEmptyReplica_AreDistinctReds()
    {
        var check = new ZfsReplicationCheck(
            [State(source: null, target: null)], [Expect("data")]);

        var findings = check.Evaluate(Inventory()).ToList();

        Assert.Equal(
            ["zfs-replication/no-snapshots", "zfs-replication/replica-missing"],
            findings.Select(f => f.RuleId).ToList());
    }

    [Fact]
    public void SnapshotOnlyEntry_JudgesNoReplica()
    {
        var check = new ZfsReplicationCheck(
            [State(source: Now.AddHours(-1), targetDataset: null)], [Expect("data")]);

        Assert.Empty(check.Evaluate(Inventory()));
    }

    // ---------- config validation ----------

    [Fact]
    public void HalfATarget_IsAConfigError()
    {
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null,
            [new ZfsReplicationConfig("d", "pve", "tank/data", TargetAlias: "nas")]);

        var error = Assert.Single(config.Validate());
        Assert.Contains("BOTH targetAlias and targetDataset", error);
    }

    [Fact]
    public void EmptyFieldsAreCaughtWithFieldNames()
    {
        var config = new RestoreGuardConfig([], null, null, 26, null, null, null, null, null, null,
            [new ZfsReplicationConfig("", "", "")]);

        var errors = config.Validate();

        Assert.Contains("zfsReplications[0].name is empty.", errors);
        Assert.Contains("zfsReplications[0].sourceAlias is empty.", errors);
        Assert.Contains("zfsReplications[0].sourceDataset is empty.", errors);
    }
}
