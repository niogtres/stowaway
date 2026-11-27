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
    [InlineData("M:/", "/mnt/mdrive", "Entire drive")]
    [InlineData("/run/desktop/mnt/host/u", "/mnt/u", "Entire drive")]
    public void Plumbing_and_whole_drives_are_excluded(string source, string destination, string reasonPrefix)
    {
        var reason = HostPaths.BindExclusionReason(source, destination);
        Assert.NotNull(reason);
        Assert.StartsWith(reasonPrefix, reason);
    }

    [Theory]
    [InlineData("/run/desktop/mnt/host/x/Dev/blog/src", "/var/www/html")]
    [InlineData(@"X:\Dev\wiki\config", "/config")]
    [InlineData("/srv/data", "/data")]
    public void Ordinary_data_paths_are_included(string source, string destination) =>
        Assert.Null(HostPaths.BindExclusionReason(source, destination));
}
