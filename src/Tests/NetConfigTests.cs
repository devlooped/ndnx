using ndx;

namespace Tests;

public class NetConfigTests
{
    [Fact]
    public void Missing_file_is_default_five_seconds()
    {
        var dir = NewDir();
        var interval = NetConfig.ReadUpdateInterval(dir, userProfile: dir);
        Assert.Equal(NetConfig.DefaultUpdateInterval, interval);
    }

    [Fact]
    public void Reads_interval_from_working_directory_netconfig()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, ".netconfig"),
            """
            [ndx]
                interval = 2
            """);

        var interval = NetConfig.ReadUpdateInterval(dir, userProfile: Path.Combine(dir, "nouser"));
        Assert.Equal(TimeSpan.FromSeconds(2), interval);
    }

    [Fact]
    public void Quoted_and_commented_values_parse()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, ".netconfig"),
            """
            # comment
            [ndx]
                interval = "7" ; seconds
            """);

        var interval = NetConfig.ReadUpdateInterval(dir, userProfile: Path.Combine(dir, "nouser"));
        Assert.Equal(TimeSpan.FromSeconds(7), interval);
    }

    [Fact]
    public void Invalid_or_non_positive_interval_is_ignored()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, ".netconfig"),
            """
            [ndx]
                interval = 0
            """);

        var interval = NetConfig.ReadUpdateInterval(dir, userProfile: Path.Combine(dir, "nouser"));
        Assert.Equal(NetConfig.DefaultUpdateInterval, interval);
    }

    [Fact]
    public void Walks_up_to_parent_netconfig()
    {
        var root = NewDir();
        File.WriteAllText(Path.Combine(root, ".netconfig"),
            """
            [ndx]
                interval = 3
            """);
        var child = Path.Combine(root, "sub");
        Directory.CreateDirectory(child);

        var interval = NetConfig.ReadUpdateInterval(child, userProfile: Path.Combine(root, "nouser"));
        Assert.Equal(TimeSpan.FromSeconds(3), interval);
    }

    [Fact]
    public void User_profile_is_used_when_cwd_has_no_interval()
    {
        var cwd = NewDir();
        var home = NewDir();
        File.WriteAllText(Path.Combine(home, ".netconfig"),
            """
            [ndx]
                interval = 9
            """);

        var interval = NetConfig.ReadUpdateInterval(cwd, userProfile: home);
        Assert.Equal(TimeSpan.FromSeconds(9), interval);
    }

    [Fact]
    public void Other_sections_are_ignored()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, ".netconfig"),
            """
            [evergreen]
                interval = 1
            [ndx "other"]
                interval = 2
            """);

        var interval = NetConfig.ReadUpdateInterval(dir, userProfile: Path.Combine(dir, "nouser"));
        Assert.Equal(NetConfig.DefaultUpdateInterval, interval);
    }

    static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ndx-netconfig", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
