using System.Globalization;

namespace ndnx;

/// <summary>
/// Tiny git-config / <c>.netconfig</c> reader for the <c>[ndnx]</c> section.
/// </summary>
public static class NetConfig
{
    public static readonly TimeSpan DefaultUpdateInterval = TimeSpan.FromSeconds(5);

    public static TimeSpan ReadUpdateInterval(string workingDirectory, string? userProfile = null)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        foreach (var path in Enumerate(workingDirectory, userProfile))
        {
            if (TryReadNumber(path, "ndnx", "interval", out var seconds) && seconds > 0)
                return TimeSpan.FromSeconds(seconds);
        }

        return DefaultUpdateInterval;
    }

    public static IEnumerable<string> Enumerate(string workingDirectory, string? userProfile = null)
    {
        var directory = new DirectoryInfo(workingDirectory);
        while (directory is not null)
        {
            yield return Path.Combine(directory.FullName, ".netconfig");
            directory = directory.Parent;
        }

        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
            yield return Path.Combine(userProfile, ".netconfig");
    }

    public static bool TryReadNumber(string path, string section, string key, out double value)
    {
        value = 0;
        if (!File.Exists(path))
            return false;

        string? current = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                current = line[1..^1].Trim();
                continue;
            }

            if (!section.Equals(current, StringComparison.OrdinalIgnoreCase))
                continue;

            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;

            var name = line[..eq].Trim();
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var text = Unquote(line[(eq + 1)..].Trim());
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;
        }

        return false;
    }

    static string StripComment(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
                inQuote = !inQuote;
            else if (!inQuote && c is '#' or ';')
                return line[..i];
        }

        return line;
    }

    static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }
}
