using System.Xml.Linq;

namespace ndnx;

/// <summary>
/// Resolves the NuGet global packages folder the same way restore does,
/// minus the user-level config walk: <c>NUGET_PACKAGES</c>, then
/// <c>config.globalPackagesFolder</c> in nuget.config, then
/// <c>~/.nuget/packages</c>.
/// </summary>
public static class GlobalPackagesFolder
{
    public static string Resolve(string? workingDirectory = null, string? configFile = null)
    {
        var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);

        workingDirectory ??= Directory.GetCurrentDirectory();
        var configPath = configFile is { } explicitConfig
            ? Path.GetFullPath(explicitConfig, workingDirectory)
            : PackageSources.FindNuGetConfig(workingDirectory);

        if (configPath is not null && File.Exists(configPath)
            && TryReadConfiguredFolder(configPath, out var configured))
        {
            return configured;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget",
            "packages");
    }

    static bool TryReadConfiguredFolder(string configPath, out string folder)
    {
        folder = "";
        try
        {
            var document = XDocument.Load(configPath);
            foreach (var item in document.Root?.Element("config")?.Elements("add") ?? [])
            {
                var key = (string?)item.Attribute("key");
                var value = (string?)item.Attribute("value");
                if (value is null || !string.Equals(key, "globalPackagesFolder", StringComparison.OrdinalIgnoreCase))
                    continue;

                var configDirectory = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();
                folder = Path.GetFullPath(value, configDirectory);
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }
}
