using System.Globalization;

namespace ndnx;

/// <summary>
/// Renders HTTP nupkg download progress: a byte bar when the total is known,
/// otherwise a spinner plus "downloading".
/// </summary>
public static class DownloadProgress
{
    public const string Downloading = "downloading";

    const int BarWidth = 20;
    static readonly char[] SpinnerFrames = ['|', '/', '-', '\\'];

    public static string Render(long bytesTransferred, long? totalBytes, int frame = 0)
    {
        if (totalBytes is > 0)
            return RenderBar(bytesTransferred, totalBytes.Value);

        var spinner = SpinnerFrames[Math.Abs(frame) % SpinnerFrames.Length];
        return $"{spinner} {Downloading}";
    }

    static string RenderBar(long transferred, long total)
    {
        var ratio = Math.Clamp((double)transferred / total, 0, 1);
        var filled = (int)Math.Round(ratio * BarWidth);
        if (filled > BarWidth)
            filled = BarWidth;

        var bar = string.Create(BarWidth + 2, filled, static (span, count) =>
        {
            span[0] = '[';
            span[^1] = ']';
            span[1..^1].Fill('-');
            if (count > 0)
                span.Slice(1, count).Fill('#');
        });

        return $"{bar} {FormatBytes(transferred)} / {FormatBytes(total)}";
    }

    static string FormatBytes(long bytes)
    {
        ReadOnlySpan<string> units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit == 0)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }
}

sealed class DownloadProgressWriter
{
    readonly TextWriter output;
    int frame;
    int displayed;

    public DownloadProgressWriter(TextWriter output) => this.output = output;

    public void Report(long bytesTransferred, long? totalBytes)
    {
        WriteFrame(DownloadProgress.Render(bytesTransferred, totalBytes, frame++));
    }

    public void Complete()
    {
        if (displayed == 0)
            return;

        output.Write('\r');
        output.Write(new string(' ', displayed));
        output.Write('\r');
        output.Flush();
        displayed = 0;
    }

    void WriteFrame(string text)
    {
        var width = Math.Max(text.Length, displayed);
        output.Write('\r');
        output.Write(text);
        if (width > text.Length)
            output.Write(new string(' ', width - text.Length));
        output.Flush();
        displayed = text.Length;
    }
}
