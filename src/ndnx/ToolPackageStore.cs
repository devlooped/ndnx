using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace ndnx;

/// <summary>
/// Downloads a tool package into a local cache (no PATH / global install) and
/// locates the packaged command.
/// </summary>
public sealed class ToolPackageStore
{
    public const string ReadyMarker = ".ndnx-ok";

    readonly PackageFeed feed;
    readonly string storeDirectory;
    readonly TextWriter? log;

    public ToolPackageStore(PackageFeed feed, string storeDirectory, TextWriter? log = null)
    {
        this.feed = feed;
        this.storeDirectory = storeDirectory;
        this.log = log;
    }

    public string StoreDirectory => storeDirectory;

    public async Task<ToolCommand> GetAsync(
        Invocation invocation,
        IReadOnlyList<string> sources,
        CancellationToken cancellationToken = default)
    {
        var range = VersionRange.FromInvocation(invocation);
        var candidates = await feed.ListAsync(sources, invocation.PackageId!, range, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Package '{invocation.PackageId}' was not found on the configured sources.");
        }

        var selected = candidates
            .OrderByDescending(c => c.Version)
            .First();

        var packageDirectory = GetPackageDirectory(selected.Id, selected.Version);
        if (!IsCached(packageDirectory))
        {
            log?.WriteLine($"Downloading {selected.Id} {selected.Version} from {selected.Source}");
            await DownloadAndExtractAsync(selected, packageDirectory, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            log?.WriteLine($"Using cached {selected.Id} {selected.Version}");
        }

        return LocateCommand(packageDirectory, selected);
    }

    public string GetPackageDirectory(string packageId, PackageVersion version)
        => Path.Combine(storeDirectory, packageId.ToLowerInvariant(), version.ToString().ToLowerInvariant());

    public static bool IsCached(string packageDirectory)
        => File.Exists(Path.Combine(packageDirectory, ReadyMarker));

    async Task DownloadAndExtractAsync(PackageIdentity package, string packageDirectory, CancellationToken cancellationToken)
    {
        var staging = packageDirectory + ".staging";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        var nupkg = Path.Combine(staging, $"{package.Id.ToLowerInvariant()}.{package.Version}.nupkg");
        await feed.DownloadAsync(package, nupkg, cancellationToken).ConfigureAwait(false);
        ZipFile.ExtractToDirectory(nupkg, staging);

        if (Directory.Exists(packageDirectory))
            Directory.Delete(packageDirectory, recursive: true);

        Directory.CreateDirectory(Path.GetDirectoryName(packageDirectory)!);
        Directory.Move(staging, packageDirectory);
        File.WriteAllText(Path.Combine(packageDirectory, ReadyMarker), package.Version.ToString());
    }

    internal static ToolCommand LocateCommand(string packageDirectory, PackageIdentity package)
    {
        var settingsPath = FindSettings(packageDirectory)
            ?? throw new InvalidOperationException(
                $"Package {package.Id} {package.Version} is missing DotnetToolSettings.xml.");

        var settings = ReadSettings(settingsPath);
        if (settings.RidPackages.Count > 0 && string.IsNullOrEmpty(settings.Runner))
        {
            throw new InvalidOperationException(
                $"Package {package.Id} {package.Version} declares RID-specific packages, which are not resolved by this runner. Use a classic tools/ TFM/RID layout.");
        }

        if (string.IsNullOrEmpty(settings.Name) || string.IsNullOrEmpty(settings.EntryPoint) || string.IsNullOrEmpty(settings.Runner))
        {
            throw new InvalidOperationException(
                $"Package {package.Id} {package.Version} has an incomplete DotnetToolSettings.xml.");
        }

        var entryPoint = Path.Combine(Path.GetDirectoryName(settingsPath)!, settings.EntryPoint);
        if (!File.Exists(entryPoint))
        {
            throw new InvalidOperationException(
                $"Tool entry point '{settings.EntryPoint}' was not found in {package.Id} {package.Version}.");
        }

        return new ToolCommand(settings.Name, entryPoint, settings.Runner);
    }

    static string? FindSettings(string packageDirectory)
    {
        var files = Directory.GetFiles(packageDirectory, "DotnetToolSettings.xml", SearchOption.AllDirectories);
        if (files.Length == 0)
            return null;

        var rid = RuntimeInformation.RuntimeIdentifier;
        return files
            .OrderByDescending(Score)
            .First();

        int Score(string path)
        {
            var score = 0;
            var normalized = path.Replace('\\', '/');
            if (normalized.Contains('/' + rid + '/', StringComparison.OrdinalIgnoreCase))
                score += 1000;
            else if (normalized.Contains("/any/", StringComparison.OrdinalIgnoreCase))
                score += 500;

            foreach (var (tfm, points) in new[]
            {
                ("net11.0", 110),
                ("net10.0", 100),
                ("net9.0", 90),
                ("net8.0", 80),
                ("net7.0", 70),
                ("net6.0", 60),
                ("netcoreapp", 10),
            })
            {
                if (normalized.Contains('/' + tfm + '/', StringComparison.OrdinalIgnoreCase))
                {
                    score += points;
                    break;
                }
            }

            return score;
        }
    }

    static ToolSettings ReadSettings(string path)
    {
        var document = XDocument.Load(path);
        var root = document.Root ?? throw new InvalidOperationException($"Invalid tool settings: {path}");
        var command = root.Element("Commands")?.Element("Command");
        var ridPackages = root.Element("RuntimeIdentifierPackages")
            ?.Elements("RuntimeIdentifierPackage")
            .Select(e => ((string?)e.Attribute("RuntimeIdentifier"), (string?)e.Attribute("Id")))
            .Where(p => p.Item1 is not null && p.Item2 is not null)
            .ToDictionary(p => p.Item1!, p => p.Item2!, StringComparer.OrdinalIgnoreCase)
            ?? [];

        return new ToolSettings(
            (string?)command?.Attribute("Name"),
            (string?)command?.Attribute("EntryPoint"),
            (string?)command?.Attribute("Runner"),
            ridPackages);
    }

    sealed record ToolSettings(
        string? Name,
        string? EntryPoint,
        string? Runner,
        IReadOnlyDictionary<string, string> RidPackages);
}
