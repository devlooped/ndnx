using System.Diagnostics;

namespace ndx;

/// <summary>
/// The <c>dotnet</c> muxer that will <c>exec</c> a framework-dependent tool.
/// Discovery matches <see cref="ToolLauncher"/>: <c>DOTNET_ROOT</c>, then PATH.
/// Runtimes are read from <c>shared/Microsoft.NETCore.App</c> next to that muxer.
/// </summary>
public static class DotnetMuxer
{
    public const string NetCoreApp = "Microsoft.NETCore.App";

    public static string FileName => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    public static string? Resolve(string? dotnetRoot = null, string? path = null)
    {
        foreach (var root in new[]
        {
            dotnetRoot ?? Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"),
        })
        {
            if (string.IsNullOrEmpty(root))
                continue;

            var candidate = Path.Combine(root, FileName);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        path ??= Environment.GetEnvironmentVariable("PATH");
        if (path is null)
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, FileName);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    public static IReadOnlyList<PackageVersion> ListNetCoreApp(string? muxerPath)
    {
        if (string.IsNullOrEmpty(muxerPath))
            return [];

        var root = Path.GetDirectoryName(muxerPath);
        if (string.IsNullOrEmpty(root))
            return [];

        var shared = Path.Combine(root, "shared", NetCoreApp);
        if (!Directory.Exists(shared))
            return ListFromCommand(muxerPath);

        var versions = new List<PackageVersion>();
        foreach (var directory in Directory.EnumerateDirectories(shared))
        {
            if (PackageVersion.TryParse(Path.GetFileName(directory), out var version))
                versions.Add(version);
        }

        versions.Sort();
        versions.Reverse();
        return versions;
    }

    public static FrameworkMoniker? HighestNetCoreApp(string? muxerPath)
    {
        var versions = ListNetCoreApp(muxerPath);
        return versions.Count == 0
            ? null
            : new FrameworkMoniker(versions[0].Major, versions[0].Minor);
    }

    public static PackageVersion? HighestNetCoreAppVersion(string? muxerPath)
    {
        var versions = ListNetCoreApp(muxerPath);
        return versions.Count == 0 ? null : versions[0];
    }

    static IReadOnlyList<PackageVersion> ListFromCommand(string muxerPath)
    {
        if (!File.Exists(muxerPath))
            return [];

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = muxerPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("--list-runtimes");

            using var process = Process.Start(start);
            if (process is null)
                return [];

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                return [];

            var versions = new List<PackageVersion>();
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;
                if (!parts[0].Equals(NetCoreApp, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (PackageVersion.TryParse(parts[1], out var version))
                    versions.Add(version);
            }

            versions.Sort();
            versions.Reverse();
            return versions;
        }
        catch (Exception)
        {
            return [];
        }
    }
}
