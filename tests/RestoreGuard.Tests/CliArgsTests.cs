using RestoreGuard.Cli;

namespace RestoreGuard.Tests;

public class CliArgsTests
{
    [Fact]
    public void BareInvocationIsInteractive()
    {
        var parsed = CliArgs.Parse([]);
        Assert.Equal(("interactive", "restoreguard.json", false), (parsed.Command, parsed.ConfigPath, parsed.Json));
        Assert.Null(parsed.Error);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("doctor")]
    [InlineData("init")]
    public void VerbsParse(string verb)
    {
        var parsed = CliArgs.Parse([verb]);
        Assert.Equal(verb, parsed.Command);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void FlagsWithoutVerbMeanAudit()
    {
        var parsed = CliArgs.Parse(["--json", "-c", "mylab.json"]);
        Assert.Equal(("audit", "mylab.json", true), (parsed.Command, parsed.ConfigPath, parsed.Json));
    }

    [Fact]
    public void VerbWithOptionsParses()
    {
        var parsed = CliArgs.Parse(["audit", "--json", "--config", "lab.json"]);
        Assert.Equal(("audit", "lab.json", true), (parsed.Command, parsed.ConfigPath, parsed.Json));
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpAliasesParse(string arg)
    {
        Assert.Equal("help", CliArgs.Parse([arg]).Command);
    }

    [Fact]
    public void UnknownArgumentErrors()
    {
        var parsed = CliArgs.Parse(["--halp"]);
        Assert.Equal("help", parsed.Command);
        Assert.Contains("--halp", parsed.Error);
    }

    [Fact]
    public void ConfigWithoutPathErrors()
    {
        Assert.NotNull(CliArgs.Parse(["audit", "-c"]).Error);
    }
}
