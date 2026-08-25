using ndnx;

namespace Tests;

public class DownloadProgressTests
{
    [Fact]
    public void Known_total_renders_byte_bar_with_transferred_and_total()
    {
        var start = DownloadProgress.Render(0, 100, 0);
        var mid = DownloadProgress.Render(50, 100, 0);
        var done = DownloadProgress.Render(100, 100, 0);

        AssertBar(start, "0 B", "100 B");
        AssertBar(mid, "50 B", "100 B");
        AssertBar(done, "100 B", "100 B");
        Assert.DoesNotContain("downloading", mid, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_total_renders_spinner_and_downloading()
    {
        var frame = DownloadProgress.Render(12_345, totalBytes: null, frame: 0);

        Assert.Contains("downloading", frame, StringComparison.Ordinal);
        Assert.Matches(@"^[|/\-\\] downloading$", frame);
        Assert.DoesNotContain('[', frame);
    }

    [Fact]
    public void Spinner_frames_advance()
    {
        var first = DownloadProgress.Render(1, null, 0);
        var second = DownloadProgress.Render(2, null, 1);
        var fifth = DownloadProgress.Render(3, null, 4);

        Assert.NotEqual(first, second);
        Assert.Equal(first, fifth);
        Assert.Contains("downloading", first, StringComparison.Ordinal);
        Assert.Contains("downloading", second, StringComparison.Ordinal);
    }

    static void AssertBar(string frame, string transferred, string total)
    {
        Assert.Matches(@"^\[#*\-*\] .+$", frame);
        Assert.Contains(transferred, frame, StringComparison.Ordinal);
        Assert.Contains(total, frame, StringComparison.Ordinal);
        Assert.Contains(" / ", frame, StringComparison.Ordinal);
    }
}
