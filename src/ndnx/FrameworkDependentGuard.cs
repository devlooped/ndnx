using System.Text.Json;

namespace ndnx;

/// <summary>
/// Best-effort preflight for <c>Runner=dotnet</c>: can the resolved muxer's
/// <c>Microsoft.NETCore.App</c> load this tool? Native / executable tools skip this.
/// </summary>
public static class FrameworkDependentGuard
{
    public static void EnsureCanExecute(string entryPointPath, string settingsPath, string? muxerPath)
    {
        var required = ReadRequirement(entryPointPath, settingsPath);
        if (muxerPath is null || !File.Exists(muxerPath))
        {
            throw new InvalidOperationException(
                required is { } tfm
                    ? $"This tool requires {tfm.Display}. Could not find '{DotnetMuxer.FileName}' on PATH or DOTNET_ROOT."
                    : $"This tool is framework-dependent and needs a .NET runtime. Could not find '{DotnetMuxer.FileName}' on PATH or DOTNET_ROOT.");
        }

        if (required is not { } need)
            return;

        var installed = DotnetMuxer.HighestNetCoreAppVersion(muxerPath);
        var host = DotnetMuxer.HighestNetCoreApp(muxerPath);
        if (host is not { } have || installed is null || have < need)
            throw new InvalidOperationException(FormatIncompatible(need, muxerPath, installed));
    }

    public static FrameworkMoniker? ReadRequirement(string entryPointPath, string settingsPath)
        => ReadFromRuntimeConfig(entryPointPath) ?? ReadFromToolsPath(settingsPath);

    public static FrameworkMoniker? ReadFromRuntimeConfig(string entryPointPath)
    {
        var configPath = Path.ChangeExtension(entryPointPath, ".runtimeconfig.json");
        if (!File.Exists(configPath))
            return null;

        try
        {
            using var stream = File.OpenRead(configPath);
            var file = JsonSerializer.Deserialize(stream, NuGetJsonContext.Default.RuntimeConfigFile);
            var options = file?.RuntimeOptions;
            if (options is null)
                return null;

            if (TryFramework(options.Framework, out var fromFramework))
                return fromFramework;

            if (options.Frameworks is { Length: > 0 })
            {
                foreach (var framework in options.Frameworks)
                {
                    if (TryFramework(framework, out var parsed))
                        return parsed;
                }
            }

            if (FrameworkMoniker.TryParseFolder(options.Tfm, out var fromTfm))
                return fromTfm;
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static FrameworkMoniker? ReadFromToolsPath(string settingsPath)
        => ToolSettingsLocator.TryParseToolsPath(settingsPath, out var tfm, out _) ? tfm : null;

    public static string FormatIncompatible(FrameworkMoniker required, string muxerPath, PackageVersion? installed)
    {
        if (installed is { } version)
        {
            return $"This tool requires {required.Display}. The '{DotnetMuxer.FileName}' at '{muxerPath}' has {DotnetMuxer.NetCoreApp} {version}.";
        }

        return $"This tool requires {required.Display}. No {DotnetMuxer.NetCoreApp} runtimes were found next to '{muxerPath}'.";
    }

    static bool TryFramework(RuntimeConfigFramework? framework, out FrameworkMoniker moniker)
    {
        moniker = default;
        if (framework?.Name is null
            || !framework.Name.Equals(DotnetMuxer.NetCoreApp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!FrameworkMoniker.TryParseVersion(framework.Version, out var parsed) || parsed is null)
            return false;

        moniker = parsed.Value;
        return true;
    }
}
