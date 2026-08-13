using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ndnx;

/// <summary>
/// NuGet-compatible enough package version (major.minor.patch[-release][+metadata]).
/// </summary>
public readonly record struct PackageVersion(int Major, int Minor, int Patch, string? Release) : IComparable<PackageVersion>
{
    public bool IsPrerelease => !string.IsNullOrEmpty(Release);

    public override string ToString() => IsPrerelease
        ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Release}")
        : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    public int CompareTo(PackageVersion other)
    {
        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0)
            return cmp;
        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0)
            return cmp;
        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0)
            return cmp;

        if (IsPrerelease != other.IsPrerelease)
            return IsPrerelease ? -1 : 1;

        if (!IsPrerelease)
            return 0;

        return string.Compare(Release, other.Release, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out PackageVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];

        string? release = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            release = text[(dash + 1)..];
            text = text[..dash];
            if (release.Length == 0)
                return false;
        }

        var parts = text.Split('.');
        if (parts.Length is < 2 or > 4)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
            return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
            return false;

        var patch = 0;
        if (parts.Length >= 3 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
            return false;

        // Ignore a 4th revision component if present (NuGet sometimes uses 4-part versions).
        if (parts.Length == 4 && !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return false;

        version = new PackageVersion(major, minor, patch, release);
        return true;
    }
}

/// <summary>
/// Inclusive/exclusive min/max version range. Bare versions are exact.
/// </summary>
public readonly record struct VersionRange(PackageVersion? Min, bool IncludeMin, PackageVersion? Max, bool IncludeMax, bool IncludePrerelease)
{
    public bool IsExact => Min is { } min && Max is { } max && IncludeMin && IncludeMax && min.Equals(max);

    public bool Matches(PackageVersion version)
    {
        if (version.IsPrerelease && !IncludePrerelease && !IsExact)
            return false;

        if (Min is { } min)
        {
            var cmp = version.CompareTo(min);
            if (cmp < 0 || (cmp == 0 && !IncludeMin))
                return false;
        }

        if (Max is { } max)
        {
            var cmp = version.CompareTo(max);
            if (cmp > 0 || (cmp == 0 && !IncludeMax))
                return false;
        }

        return true;
    }

    public static VersionRange Exact(PackageVersion version) => new(version, true, version, true, version.IsPrerelease);

    public static VersionRange Any(bool includePrerelease) => new(null, true, null, true, includePrerelease);

    public static bool TryParse(string? value, out VersionRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text is "*" or "*-*")
        {
            range = Any(includePrerelease: text == "*-*");
            return true;
        }

        if (text[0] is '[' or '(' && text[^1] is ']' or ')')
        {
            var includeMin = text[0] == '[';
            var includeMax = text[^1] == ']';
            var inner = text[1..^1];
            var comma = inner.IndexOf(',');
            if (comma < 0)
            {
                if (!PackageVersion.TryParse(inner, out var exact))
                    return false;
                range = new VersionRange(exact, includeMin, exact, includeMax, exact.IsPrerelease);
                return true;
            }

            var left = inner[..comma].Trim();
            var right = inner[(comma + 1)..].Trim();
            PackageVersion? min = null;
            PackageVersion? max = null;
            if (left.Length > 0)
            {
                if (!PackageVersion.TryParse(left, out var parsedMin))
                    return false;
                min = parsedMin;
            }

            if (right.Length > 0)
            {
                if (!PackageVersion.TryParse(right, out var parsedMax))
                    return false;
                max = parsedMax;
            }

            var prerelease = (min?.IsPrerelease ?? false) || (max?.IsPrerelease ?? false);
            range = new VersionRange(min, includeMin, max, includeMax, prerelease);
            return true;
        }

        if (PackageVersion.TryParse(text, out var version))
        {
            range = Exact(version);
            return true;
        }

        return false;
    }

    public static VersionRange FromInvocation(Invocation invocation)
    {
        if (invocation.Version is { } specified)
        {
            if (PackageVersion.TryParse(specified, out var exact))
                return Exact(exact);
            if (TryParse(specified, out var parsed))
                return parsed with { IncludePrerelease = parsed.IncludePrerelease || invocation.Prerelease };
        }

        return Any(invocation.Prerelease);
    }
}
