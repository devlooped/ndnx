using System.Xml.Linq;

namespace ndx;

/// <summary>
/// Resolves NuGet sources from --source / --add-source / --configfile / nuget.config.
/// </summary>
public static class PackageSources
{
    public const string NugetOrg = "https://api.nuget.org/v3/index.json";

    public static IReadOnlyList<string> Resolve(Invocation invocation, string workingDirectory)
    {
        var sources = new List<string>();

        if (invocation.Sources.Count > 0)
        {
            sources.AddRange(invocation.Sources.Select(s => Normalize(s, workingDirectory)));
        }
        else
        {
            var configPath = invocation.ConfigFile is { } explicitConfig
                ? Path.GetFullPath(explicitConfig, workingDirectory)
                : FindNuGetConfig(workingDirectory);

            if (configPath is not null && File.Exists(configPath))
                sources.AddRange(ReadConfigSources(configPath));
        }

        foreach (var extra in invocation.AddSources)
        {
            var normalized = Normalize(extra, workingDirectory);
            if (!sources.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                sources.Add(normalized);
        }

        if (sources.Count == 0)
            sources.Add(NugetOrg);

        return sources;
    }

    public static string? FindNuGetConfig(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "nuget.config");
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(directory.FullName, "NuGet.Config");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return null;
    }

    public static IReadOnlyList<string> ReadConfigSources(string configPath)
    {
        var document = XDocument.Load(configPath);
        var root = document.Root;
        if (root is null)
            return [];

        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.Element("disabledPackageSources")?.Elements("add") ?? [])
        {
            var key = (string?)item.Attribute("key");
            var value = (string?)item.Attribute("value");
            if (key is not null && value is not null &&
                (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"))
            {
                disabled.Add(key);
            }
        }

        var sources = new List<string>();
        var configDirectory = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();
        foreach (var item in root.Element("packageSources")?.Elements("add") ?? [])
        {
            var key = (string?)item.Attribute("key");
            var value = (string?)item.Attribute("value");
            if (value is null || (key is not null && disabled.Contains(key)))
                continue;

            sources.Add(Normalize(value, configDirectory));
        }

        return sources;
    }

    static string Normalize(string source, string baseDirectory)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return source;
        }

        return Path.GetFullPath(source, baseDirectory);
    }
}
