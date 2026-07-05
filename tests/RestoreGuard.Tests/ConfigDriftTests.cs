using RestoreGuard.Checks;
using RestoreGuard.Core;
using RestoreGuard.Core.Model;
using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Tests;

public class ConfigDriftTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 23, 30, 0, TimeSpan.Zero);

    private static Service WithHashes(string? live, string? declared, string state = "running") =>
        new("vaultwarden", "lab98", ServiceKind.Container, state, [], [], "vaultwarden/server",
            ComposeProject: "vaultwarden", ConfigHashLive: live, ConfigHashDeclared: declared);

    [Fact]
    public void GoldenFile_HashOutputParses()
    {
        var hashes = ComposeConfigParser.ParseHashes(Fixtures.Read("compose-hashes-mulberryheron.txt"));

        Assert.Equal(7, hashes.Count);
        Assert.Equal("5a456756a7e8896c53cb1f388cd0c7175d07801baea59638a2f5557ade3de86b", hashes["postgres"]);
    }

    [Fact]
    public void GoldenFile_AssemblerJoinsLabelHashWithCurrentHash()
    {
        var containers = DockerInspectParser.Parse(Fixtures.Read("docker-inspect-lab55.json"));
        var project = ComposeConfigParser.Parse(Fixtures.Read("compose-config-mulberryheron-prod.json"))
            with { ServiceHashes = ComposeConfigParser.ParseHashes(Fixtures.Read("compose-hashes-mulberryheron.txt")) };

        var services = ServiceAssembler.Build("lab55", containers,
            new Dictionary<string, ComposeProjectConfig?> { ["mulberryheron-prod"] = project });

        var pg = Assert.Single(services, s => s.Name == "mulberryheron-prod-postgres-1");
        Assert.NotNull(pg.ConfigHashLive);
        // postgres wasn't redeployed between captures: label == file hash → no drift.
        Assert.Equal(pg.ConfigHashLive, pg.ConfigHashDeclared);

        // The app container in the inspect capture predates a prod redeploy that
        // happened before the hash capture — a genuine stale-config pair: the file
        // no longer matches what this container was created from.
        var finding = Assert.Single(new ConfigDriftCheck().Evaluate(new LabInventory(Now, services, [], [])));
        Assert.Equal(("config-drift/stale-config", "mulberryheron-prod-app-1"), (finding.RuleId, finding.Service));
    }

    [Fact]
    public void EditedComposeFileIsYellowStaleConfig()
    {
        var svc = WithHashes(live: "aaaa1111", declared: "bbbb2222");

        var finding = Assert.Single(new ConfigDriftCheck().Evaluate(new LabInventory(Now, [svc], [], [])));
        Assert.Equal(("config-drift/stale-config", Severity.Yellow), (finding.RuleId, finding.Severity));
        Assert.Contains("aaaa1111", finding.Evidence);
    }

    [Fact]
    public void MissingHashesAndStoppedContainersAreSkipped()
    {
        var inventory = new LabInventory(Now,
        [
            WithHashes(live: "aaaa", declared: null),
            WithHashes(live: null, declared: "bbbb"),
            WithHashes(live: "aaaa", declared: "bbbb", state: "exited"),
        ], [], []);

        Assert.Empty(new ConfigDriftCheck().Evaluate(inventory));
    }
}
