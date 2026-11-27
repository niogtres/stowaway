using Stowaway.Core.Inventory;

namespace Stowaway.Tests.Inventory;

public sealed class HostPathsTests
{
    [Theory]
    [InlineData("/run/desktop/mnt/host/x/Dev/wowq/data", @"X:\Dev\wowq\data")]
    [InlineData("/run/desktop/mnt/host/d/PlexMS/Plex Media Server", @"D:\PlexMS\Plex Media Server")]
    [InlineData("/host_mnt/c/Users/me/app/", @"C:\Users\me\app")]
    [InlineData(@"X:\Dev\photos\release", @"X:\Dev\photos\release")]
    [InlineData("x:/Dev/app", @"X:\Dev\app")]
    [InlineData("M:/", @"M:\")]
    [InlineData("/run/desktop/mnt/host/m", @"M:\")]
    [InlineData(@"\\nas\share\docker", @"\\nas\share\docker")]
    public void Maps_engine_sources_to_windows_paths(string source, string expected) =>
        Assert.Equal(expected, HostPaths.ToWindowsPath(source));

    [Theory]
    [InlineData("/var/lib/app")]
    [InlineData("/run/host-services/docker.proxy.sock")]
    [InlineData("")]
    public void Engine_only_paths_have_no_windows_equivalent(string source) =>
        Assert.Null(HostPaths.ToWindowsPath(source));

    [Theory]
    [InlineData("/var/run/docker.sock", "/var/run/docker.sock", "Socket")]
    [InlineData("/run/host-services/docker.proxy.sock", "/var/run/docker.sock", "Socket")]
    [InlineData(@"\\.\pipe\docker_engine", @"\\.\pipe\docker_engine", "Named pipe")]
    [InlineData("/proc", "/host/proc", "System path")]
    [InlineData("/etc/localtime", "/etc/localtime", "System path")]
    [InlineData("M:/", "/mnt/mdrive", "Whole drive")]
    [InlineData("/run/desktop/mnt/host/u", "/mnt/u", "Whole drive")]
    [InlineData(@"\\nas\media", "/media", "Network share")]
    public void Plumbing_drives_and_shares_are_excluded(string source, string destination, string reasonPrefix)
    {
        var reason = HostPaths.BindExclusionReason(source, destination, projectDir: @"X:\Dev\blog");
        Assert.NotNull(reason);
        Assert.StartsWith(reasonPrefix, reason);
    }

    [Theory]
    [InlineData("/run/desktop/mnt/host/x/Dev/blog/src", @"X:\Dev\blog")]
    [InlineData(@"X:\Dev\blog", @"X:\Dev\blog")]
    [InlineData(@"x:\dev\BLOG\data\db", @"X:\Dev\blog")]
    [InlineData("/home/me/blog/data", "/home/me/blog")]
    public void Folders_inside_the_compose_project_belong_to_the_app(string source, string projectDir) =>
        Assert.Null(HostPaths.BindExclusionReason(source, "/data", projectDir));

    [Theory]
    [InlineData(@"X:\Media\Movies", @"X:\Dev\blog")]
    [InlineData(@"X:\Dev\blog-old\data", @"X:\Dev\blog")]          // a sibling with the same prefix isn't inside
    [InlineData("/run/desktop/mnt/host/x/Dev/shared", @"X:\Dev\blog")]
    [InlineData("/home/me/other", "/home/me/blog")]
    public void Folders_outside_the_project_are_not_backed_up(string source, string projectDir) =>
        Assert.Equal(HostPaths.OutsideApp, HostPaths.BindExclusionReason(source, "/data", projectDir));

    [Fact]
    public void Standalone_containers_have_no_project_folder() =>
        Assert.Equal(HostPaths.OutsideStandalone, HostPaths.BindExclusionReason(@"X:\docker\jellyfin\config", "/config", projectDir: null));
}
