using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Tests;

public class ServiceAssemblerTests
{
    [Fact]
    public void JoinsLiveContainersWithDeclaredComposeConfig()
    {
        var containers = DockerInspectParser.Parse(Fixtures.Read("docker-inspect-lab55.json"));
        var project = ComposeConfigParser.Parse(Fixtures.Read("compose-config-mulberryheron-prod.json"));
        var projects = new Dictionary<string, ComposeProjectConfig?>
        {
            ["mulberryheron-prod"] = project,
            ["crimsonraven-kc-prod"] = null, // unresolvable, like a Portainer stack
        };

        var services = ServiceAssembler.Build("lab55", containers, projects);

        var pg = Assert.Single(services, s => s.Name == "mulberryheron-prod-postgres-1");
        Assert.Equal("lab55", pg.Host);
        Assert.NotNull(pg.DeclaredMounts);
        var declared = Assert.Single(pg.DeclaredMounts!);
        // Declared volume key resolved to the canonical live name — like-for-like with LiveMounts.
        Assert.Equal("mulberryheron-prod_pgdata", declared.Source);
        Assert.Equal("/var/lib/postgresql/data", declared.Destination);
        var live = Assert.Single(pg.LiveMounts);
        Assert.Equal(declared.Source, live.Source);
        Assert.Equal(declared.Destination, live.Destination);
    }

    [Fact]
    public void UnresolvableProjectYieldsNullDeclared()
    {
        var containers = DockerInspectParser.Parse(Fixtures.Read("docker-inspect-lab55.json"));
        var services = ServiceAssembler.Build("lab55", containers,
            new Dictionary<string, ComposeProjectConfig?> { ["crimsonraven-kc-prod"] = null });

        var kc = Assert.Single(services, s => s.Name == "crimsonraven-kc-prod-postgres-1");
        Assert.Equal("crimsonraven-kc-prod", kc.ComposeProject);
        Assert.Null(kc.DeclaredMounts);
    }

    [Fact]
    public void ProjectAbsentFromResolutionMapYieldsNullDeclared()
    {
        var containers = DockerInspectParser.Parse(Fixtures.Read("docker-inspect-lab55.json"));
        var services = ServiceAssembler.Build("lab55", containers, new Dictionary<string, ComposeProjectConfig?>());

        var exited = Assert.Single(services, s => s.Name == "fuel-postgres");
        Assert.Equal("fuel", exited.ComposeProject);
        Assert.Null(exited.DeclaredMounts);
    }
}
