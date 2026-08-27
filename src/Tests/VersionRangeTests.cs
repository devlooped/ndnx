using ndx;

namespace Tests;

public class VersionRangeTests
{
    [Fact]
    public void Unspecified_version_is_the_same_range_as_star()
    {
        var bare = VersionRange.FromInvocation(ArgParser.Parse("dotnetsay"));
        var star = VersionRange.FromInvocation(ArgParser.Parse("dotnetsay@*"));
        Assert.Equal(star, bare);
        Assert.True(bare.IsAny);
        Assert.False(bare.IsExact);
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
    [InlineData("dotnetsay@1.*")]
    [InlineData("dotnetsay@1.1.*")]
    [InlineData("dotnetsay", "--version", "*")]
    [InlineData("dotnetsay", "--version", "1.*")]
    [InlineData("dotnetsay", "--version", "[1.0,2.0)")]
    public void Floating_identities_are_not_exact(params string[] args)
    {
        var range = VersionRange.FromInvocation(ArgParser.Parse(args));
        Assert.False(range.IsExact);
    }

    [Fact]
    public void Major_star_is_half_open_next_major()
    {
        var range = VersionRange.FromInvocation(ArgParser.Parse("dotnetsay@1.*"));
        Assert.False(range.IsExact);
        Assert.False(range.IsAny);
        Assert.Equal(new PackageVersion(1, 0, 0, null), range.Min);
        Assert.True(range.IncludeMin);
        Assert.Equal(new PackageVersion(2, 0, 0, null), range.Max);
        Assert.False(range.IncludeMax);
        Assert.True(range.Matches(new PackageVersion(1, 0, 0, null)));
        Assert.True(range.Matches(new PackageVersion(1, 9, 9, null)));
        Assert.False(range.Matches(new PackageVersion(2, 0, 0, null)));
        Assert.False(range.Matches(new PackageVersion(0, 9, 0, null)));
        Assert.False(range.Matches(new PackageVersion(1, 0, 1, "beta")));
    }

    [Fact]
    public void Minor_star_is_half_open_next_minor()
    {
        var range = VersionRange.FromInvocation(ArgParser.Parse("dotnetsay@1.1.*"));
        Assert.Equal(new PackageVersion(1, 1, 0, null), range.Min);
        Assert.True(range.IncludeMin);
        Assert.Equal(new PackageVersion(1, 2, 0, null), range.Max);
        Assert.False(range.IncludeMax);
        Assert.True(range.Matches(new PackageVersion(1, 1, 0, null)));
        Assert.True(range.Matches(new PackageVersion(1, 1, 99, null)));
        Assert.False(range.Matches(new PackageVersion(1, 2, 0, null)));
        Assert.False(range.Matches(new PackageVersion(1, 0, 9, null)));
    }

    [Fact]
    public void Patch_star_is_half_open_next_patch()
    {
        var range = VersionRange.FromInvocation(ArgParser.Parse("dotnetsay@1.1.1.*"));
        Assert.Equal(new PackageVersion(1, 1, 1, null), range.Min);
        Assert.Equal(new PackageVersion(1, 1, 2, null), range.Max);
        Assert.False(range.IncludeMax);
        Assert.True(range.Matches(new PackageVersion(1, 1, 1, null)));
        Assert.False(range.Matches(new PackageVersion(1, 1, 2, null)));
        Assert.False(range.Matches(new PackageVersion(1, 1, 0, null)));
    }

    [Theory]
    [InlineData("dotnetsay@1.1")]
    [InlineData("dotnetsay@1.1.0")]
    public void Two_and_three_part_versions_without_star_stay_exact(params string[] args)
    {
        var range = VersionRange.FromInvocation(ArgParser.Parse(args));
        Assert.True(range.IsExact);
        Assert.Equal(new PackageVersion(1, 1, 0, null), range.Min);
    }
}
