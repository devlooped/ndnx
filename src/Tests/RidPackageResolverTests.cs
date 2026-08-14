using System.Runtime.InteropServices;
using ndnx;

namespace Tests;

public class RidPackageResolverTests
{
    [Fact]
    public void Prefers_exact_current_host_rid()
    {
        var host = RuntimeInformation.RuntimeIdentifier;
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [host] = "pkg.host",
            ["any"] = "pkg.any",
            ["unused-rid"] = "pkg.unused",
        };

        Assert.Equal("pkg.host", RidPackageResolver.Resolve(host, declared));
    }

    [Fact]
    public void Falls_back_to_any_when_host_rid_is_not_declared()
    {
        var host = RuntimeInformation.RuntimeIdentifier;
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["unused-rid"] = "pkg.unused",
            ["any"] = "pkg.any",
        };

        Assert.Equal("pkg.any", RidPackageResolver.Resolve(host, declared));
    }

    [Fact]
    public void Returns_null_when_nothing_overlaps_the_host()
    {
        var host = RuntimeInformation.RuntimeIdentifier;
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["unused-rid"] = "pkg.unused",
            ["also-unused"] = "pkg.also",
        };

        Assert.Null(RidPackageResolver.Resolve(host, declared));
    }

    [Theory]
    [InlineData("ubuntu.24.04-x64", "linux-x64")]
    [InlineData("ubuntu.22.04-arm64", "linux-arm64")]
    [InlineData("debian.12-x64", "linux-x64")]
    [InlineData("fedora.41-x64", "linux-x64")]
    [InlineData("alpine.3.20-x64", "linux-musl-x64")]
    [InlineData("osx.15-arm64", "osx-arm64")]
    public void Distro_rid_prefers_portable_family_over_any(string host, string portable)
    {
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [portable] = "pkg.portable",
            ["any"] = "pkg.any",
            ["unused-rid"] = "pkg.unused",
        };

        Assert.Equal("pkg.portable", RidPackageResolver.Resolve(host, declared));
    }

    [Fact]
    public void Linux_musl_prefers_musl_package_before_glibc()
    {
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["linux-x64"] = "pkg.glibc",
            ["linux-musl-x64"] = "pkg.musl",
            ["any"] = "pkg.any",
        };

        Assert.Equal("pkg.musl", RidPackageResolver.Resolve("linux-musl-x64", declared));
    }

    [Fact]
    public void Expand_ubuntu_includes_linux_x64_before_any()
    {
        var expanded = RidPackageResolver.Expand("ubuntu.24.04-x64").ToList();

        Assert.Equal("ubuntu.24.04-x64", expanded[0]);
        var linux = expanded.IndexOf("linux-x64");
        var any = expanded.IndexOf("any");
        Assert.InRange(linux, 1, expanded.Count - 1);
        Assert.InRange(any, linux + 1, expanded.Count - 1);
    }

    [Fact]
    public void Expand_win_x64_walks_the_portable_graph()
    {
        var expanded = RidPackageResolver.Expand("win-x64").ToList();

        Assert.Equal(["win-x64", "win", "any"], expanded.Take(3));
    }
}
