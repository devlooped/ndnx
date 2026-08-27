using ndx;

namespace Tests;

public class VersionRangeTests
{
    [Fact]
    public void Unspecified_version_is_any()
    {
        var range = VersionRange.FromInvocation(ArgParser.Parse("dotnetsay"));
        Assert.True(range.IsAny);
        Assert.False(range.IsExact);
    }

    [Fact]
    public void Star_version_is_any()
    {
        var range = VersionRange.FromInvocation(ArgParser.Parse("dotnetsay@*"));
        Assert.True(range.IsAny);
        Assert.False(range.IsExact);
    }

    [Theory]
    [InlineData("dotnetsay@1.2.3")]
    [InlineData("dotnetsay", "--version", "1.2.3")]
    public void At_version_and_version_option_are_exact(params string[] args)
    {
        var range = VersionRange.FromInvocation(ArgParser.Parse(args));
        Assert.True(range.IsExact);
        Assert.False(range.IsAny);
        Assert.Equal(new PackageVersion(1, 2, 3, null), range.Min);
        Assert.Equal(range.Min, range.Max);
    }

    [Theory]
    [InlineData("dotnetsay")]
    [InlineData("dotnetsay@*")]
    [InlineData("dotnetsay@*-*")]
    [InlineData("dotnetsay", "--version", "*")]
    [InlineData("dotnetsay", "--version", "[1.0,2.0)")]
    public void Floating_identities_are_not_exact(params string[] args)
    {
        var range = VersionRange.FromInvocation(ArgParser.Parse(args));
        Assert.False(range.IsExact);
    }
}
