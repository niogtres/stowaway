using DockerBackup.Docker;

namespace DockerBackup.Tests;

public sealed class DockerEndpointResolverTests
{
    [Fact]
    public void Defaults_to_well_known_pipes()
    {
        var candidates = DockerEndpointResolver.Candidates(null, null);
        Assert.Equal([DockerEndpointResolver.DefaultPipe, DockerEndpointResolver.DesktopLinuxPipe], candidates);
    }

    [Fact]
    public void Explicit_endpoint_is_used_alone()
    {
        var candidates = DockerEndpointResolver.Candidates("tcp://10.0.0.5:2376", "npipe://./pipe/other");
        Assert.Equal([new Uri("tcp://10.0.0.5:2376")], candidates);
    }

    [Fact]
    public void Docker_host_env_comes_first_and_is_deduplicated()
    {
        var candidates = DockerEndpointResolver.Candidates("  ", "npipe://./pipe/docker_engine");
        Assert.Equal([DockerEndpointResolver.DefaultPipe, DockerEndpointResolver.DesktopLinuxPipe], candidates);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("ssh://user@host")]
    public void Unsupported_values_are_ignored(string value)
    {
        var candidates = DockerEndpointResolver.Candidates(value, value);
        Assert.Equal(2, candidates.Count);
    }
}
