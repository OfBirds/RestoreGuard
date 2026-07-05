using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Tests;

public class DockerInspectParserTests
{
    [Fact]
    public void ParsesLab55Containers()
    {
        var containers = DockerInspectParser.Parse(Fixtures.Read("docker-inspect-lab55.json"));

        Assert.Equal(4, containers.Count);

        var mhPostgres = Assert.Single(containers, c => c.Name == "mulberryheron-prod-postgres-1");
        Assert.Equal("running", mhPostgres.Status);
        Assert.Equal("ghcr.io/ferretdb/postgres-documentdb:17", mhPostgres.Image);
        Assert.Equal("mulberryheron-prod", mhPostgres.ComposeProject);
        Assert.Equal("postgres", mhPostgres.ComposeService);
        Assert.Equal("/opt/mulberryheron/.env.prod", mhPostgres.ComposeEnvironmentFile);

        var mount = Assert.Single(mhPostgres.Mounts);
        Assert.Equal("volume", mount.Type);
        Assert.Equal("mulberryheron-prod_pgdata", mount.Name);
        Assert.Equal("/var/lib/postgresql/data", mount.Destination);
        Assert.True(mount.ReadWrite);
    }

    [Fact]
    public void ExitedContainerKeepsItsComposeLabels()
    {
        var containers = DockerInspectParser.Parse(Fixtures.Read("docker-inspect-lab55.json"));

        // Even long-exited containers carry the project they were launched from.
        var exited = Assert.Single(containers, c => c.Name == "fuel-postgres");
        Assert.Equal("fuel", exited.ComposeProject);
        Assert.StartsWith("exited", exited.Status);
    }

    [Fact]
    public void ParsesLab98BindMounts()
    {
        var containers = DockerInspectParser.Parse(Fixtures.Read("docker-inspect-lab98.json"));

        var z2m = Assert.Single(containers, c => c.Name == "zigbee2mqtt");
        Assert.Equal("exited", z2m.Status);
        Assert.Contains(z2m.Mounts, m => m.Type == "bind");
    }
}
