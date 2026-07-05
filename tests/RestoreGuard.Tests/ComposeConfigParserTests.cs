using RestoreGuard.Providers.Docker;

namespace RestoreGuard.Tests;

public class ComposeConfigParserTests
{
    [Fact]
    public void ParsesMulberryHeronProject()
    {
        var project = ComposeConfigParser.Parse(Fixtures.Read("compose-config-mulberryheron-prod.json"));

        Assert.Equal("mulberryheron-prod", project.Name);
        Assert.True(project.Services.ContainsKey("postgres"));
        Assert.True(project.Services.ContainsKey("app"));

        var postgres = project.Services["postgres"];
        var pgMount = Assert.Single(postgres.Volumes);
        Assert.Equal("volume", pgMount.Type);
        Assert.Equal("pgdata", pgMount.Source);
        Assert.Equal("/var/lib/postgresql/data", pgMount.Target);
    }

    [Fact]
    public void ResolvesCanonicalVolumeNames()
    {
        var project = ComposeConfigParser.Parse(Fixtures.Read("compose-config-mulberryheron-prod.json"));

        // Live mounts carry the canonical (project-prefixed) volume name — the parser
        // must produce the same name so drift comparison is like-for-like.
        Assert.Equal("mulberryheron-prod_pgdata", project.ResolveVolumeName("pgdata"));
    }
}
