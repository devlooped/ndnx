using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ndnx;

/// <summary>
/// Major.minor of a .NET runtime / tools TFM folder (<c>net10.0</c>, <c>netcoreapp3.1</c>).
/// </summary>
public readonly record struct FrameworkMoniker(int Major, int Minor) : IComparable<FrameworkMoniker>
{
    public string Display => string.Create(CultureInfo.InvariantCulture, $".NET {Major}.{Minor}");

    public int CompareTo(FrameworkMoniker other)
    {
        var cmp = Major.CompareTo(other.Major);
        return cmp != 0 ? cmp : Minor.CompareTo(other.Minor);
    }

    public static bool operator <(FrameworkMoniker left, FrameworkMoniker right) => left.CompareTo(right) < 0;
    public static bool operator >(FrameworkMoniker left, FrameworkMoniker right) => left.CompareTo(right) > 0;
    public static bool operator <=(FrameworkMoniker left, FrameworkMoniker right) => left.CompareTo(right) <= 0;
    public static bool operator >=(FrameworkMoniker left, FrameworkMoniker right) => left.CompareTo(right) >= 0;

    public static bool TryParseFolder(string? folder, [NotNullWhen(true)] out FrameworkMoniker? moniker)
    {
        moniker = null;
        if (string.IsNullOrWhiteSpace(folder))
            return false;

        var text = folder.Trim();
        ReadOnlySpan<char> rest;
        if (text.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
            rest = text.AsSpan("netcoreapp".Length);
        else if (text.StartsWith("net", StringComparison.OrdinalIgnoreCase)
                 && text.Length > 3
                 && char.IsAsciiDigit(text[3]))
        {
            rest = text.AsSpan(3);
        }
        else
            return false;

        var dash = rest.IndexOf('-');
        if (dash >= 0)
            rest = rest[..dash];

        if (!TryParseMajorMinor(rest, out var major, out var minor))
            return false;

        moniker = new FrameworkMoniker(major, minor);
        return true;
    }

    public static bool TryParseVersion(string? version, [NotNullWhen(true)] out FrameworkMoniker? moniker)
    {
        moniker = null;
        if (!PackageVersion.TryParse(version, out var parsed))
            return false;
        moniker = new FrameworkMoniker(parsed.Major, parsed.Minor);
        return true;
    }

    static bool TryParseMajorMinor(ReadOnlySpan<char> text, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        var dot = text.IndexOf('.');
        if (dot <= 0)
            return false;
        return int.TryParse(text[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && int.TryParse(text[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }
}
