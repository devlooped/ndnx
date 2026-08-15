using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace ndnx;

/// <summary>
/// Downloads a tool package into a local cache (no PATH / global install) and
/// locates the packaged command.
/// </summary>
public sealed class ToolPackageStore
{
    public const string ReadyMarker = V3PackageLayout.MetadataFileName;

    readonly PackageFeed feed;
    readonly string storeDirectory;
    readonly TextWriter? log;
    readonly string? muxerPath;
    readonly string hostRid;

    public ToolPackageStore(
        PackageFeed feed,
        string storeDirectory,
        TextWriter? log = null,
        string? muxerPath = null,
        string? hostRid = null)
    {
        this.feed = feed;
        this.storeDirectory = storeDirectory;
        this.log = log;
        this.muxerPath = muxerPath ?? DotnetMuxer.Resolve();
        this.hostRid = hostRid ?? RuntimeInformation.RuntimeIdentifier;
    }

    public string StoreDirectory => storeDirectory;

    public async Task<ToolCommand> GetAsync(
        Invocation invocation,
        IReadOnlyList<string> sources,
        CancellationToken cancellationToken = default)
    {
        var range = VersionRange.FromInvocation(invocation);
        var packageId = invocation.PackageId!;

        // Match dnx / GetBestPackageVersionAsync: an exact version (tool@1.2.3)
        // is already the answer. Query the feed only for '*' / unspecified /
        // ranges, or when that exact version is not installed.
        if (range.IsExact && range.Min is { } exact
            && TryCached(packageId, exact, sources, out var cached, out var cachedDirectory))
        {
            log?.WriteLine($"Using cached {cached.Id} {cached.Version}");
            return await LocateOrHopAsync(cachedDirectory, cached, sources, cancellationToken).ConfigureAwait(false);
        }

        return await ResolveFromFeedAsync(packageId, range, sources, cancellationToken).ConfigureAwait(false);
    }

    public string GetPackageDirectory(string packageId, PackageVersion version)
        => V3PackageLayout.GetInstallPath(storeDirectory, packageId, version);

    public static bool IsCached(string packageDirectory)
        => V3PackageLayout.IsInstalled(packageDirectory);

    async Task<ToolCommand> ResolveFromFeedAsync(
        string packageId,
        VersionRange range,
        IReadOnlyList<string> sources,
        CancellationToken cancellationToken)
    {
        var candidates = await feed.ListAsync(sources, packageId, range, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Package '{packageId}' was not found on the configured sources.");
        }

        var selected = candidates
            .OrderByDescending(c => c.Version)
            .First();

        var packageDirectory = GetPackageDirectory(selected.Id, selected.Version);
        if (!V3PackageLayout.IsInstalled(packageDirectory, selected.Id, selected.Version))
        {
            log?.WriteLine($"Downloading {selected.Id} {selected.Version} from {selected.Source}");
            await DownloadAndExtractAsync(selected, packageDirectory, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            log?.WriteLine($"Using cached {selected.Id} {selected.Version}");
        }

        return await LocateOrHopAsync(packageDirectory, selected, sources, cancellationToken).ConfigureAwait(false);
    }

    bool TryCached(
        string packageId,
        PackageVersion version,
        IReadOnlyList<string> sources,
        out PackageIdentity package,
        out string packageDirectory)
    {
        packageDirectory = GetPackageDirectory(packageId, version);
        if (!V3PackageLayout.IsInstalled(packageDirectory, packageId, version))
        {
            package = default!;
            return false;
        }

        var source = sources.Count > 0 ? sources[0] : "";
        package = new PackageIdentity(packageId, version, source);
        return true;
    }

    async Task<ToolCommand> LocateOrHopAsync(
        string packageDirectory,
        PackageIdentity package,
        IReadOnlyList<string> sources,
        CancellationToken cancellationToken)
    {
        var settingsPath = FindSettings(packageDirectory)
            ?? throw new InvalidOperationException(
                $"Package {package.Id} {package.Version} is missing DotnetToolSettings.xml.");

        var settings = ReadSettings(settingsPath);
        if (settings.RidPackages.Count == 0 || !string.IsNullOrEmpty(settings.Runner))
            return LocateCommand(packageDirectory, package);

        var ridPackageId = RidPackageResolver.Resolve(hostRid, settings.RidPackages);
        if (ridPackageId is null)
        {
            var declared = string.Join(' ', settings.RidPackages.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"Package {package.Id} {package.Version} has no RID-specific package for '{hostRid}'. Declared RIDs: {declared}.");
        }

        if (TryCached(ridPackageId, package.Version, sources, out var ridCached, out var ridCachedDirectory))
        {
            log?.WriteLine($"Using cached {ridCached.Id} {ridCached.Version}");
            return LocateCommand(ridCachedDirectory, ridCached);
        }

        var ridExact = VersionRange.Exact(package.Version);
        var ridCandidates = await feed.ListAsync(sources, ridPackageId, ridExact, cancellationToken).ConfigureAwait(false);
        if (ridCandidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"RID-specific package '{ridPackageId}' {package.Version} was not found on the configured sources.");
        }

        var ridSelected = ridCandidates.OrderByDescending(c => c.Version).First();
        var ridDirectory = GetPackageDirectory(ridSelected.Id, ridSelected.Version);
        if (!V3PackageLayout.IsInstalled(ridDirectory, ridSelected.Id, ridSelected.Version))
        {
            log?.WriteLine($"Downloading {ridSelected.Id} {ridSelected.Version} from {ridSelected.Source}");
            await DownloadAndExtractAsync(ridSelected, ridDirectory, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            log?.WriteLine($"Using cached {ridSelected.Id} {ridSelected.Version}");
        }

        return LocateCommand(ridDirectory, ridSelected);
    }

    async Task DownloadAndExtractAsync(PackageIdentity package, string packageDirectory, CancellationToken cancellationToken)
    {
        var staging = packageDirectory + ".staging";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        var nupkg = Path.Combine(staging, V3PackageLayout.GetPackageFileName(package.Id, package.Version));
        await feed.DownloadAsync(package, nupkg, cancellationToken).ConfigureAwait(false);
        V3PackageLayout.ExtractContent(nupkg, staging);

        var hash = V3PackageLayout.HashNupkg(nupkg);
        V3PackageLayout.WriteHash(V3PackageLayout.GetHashPath(staging, package.Id, package.Version), hash);
        V3PackageLayout.WriteMetadata(V3PackageLayout.GetMetadataPath(staging), hash, package.Source);

        if (Directory.Exists(packageDirectory))
            Directory.Delete(packageDirectory, recursive: true);

        Directory.CreateDirectory(Path.GetDirectoryName(packageDirectory)!);
        Directory.Move(staging, packageDirectory);
    }

    ToolCommand LocateCommand(string packageDirectory, PackageIdentity package)
    {
        var settingsPath = FindSettings(packageDirectory)
            ?? throw new InvalidOperationException(
                $"Package {package.Id} {package.Version} is missing DotnetToolSettings.xml.");

        var settings = ReadSettings(settingsPath);
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

        // NuGet pack writes 0644 into the nupkg; dnx/NuGet extract then marks
        // the payload executable. ZipFile.ExtractToDirectory keeps 0644.
        if (string.Equals(settings.Runner, "executable", StringComparison.OrdinalIgnoreCase))
            EnsureUnixExecuteBits(Path.GetDirectoryName(entryPoint)!);
        else if (string.Equals(settings.Runner, "dotnet", StringComparison.OrdinalIgnoreCase))
            FrameworkDependentGuard.EnsureCanExecute(entryPoint, settingsPath, muxerPath);

        return new ToolCommand(settings.Name, entryPoint, settings.Runner);
    }

    static void EnsureUnixExecuteBits(string directory)
    {
        if (OperatingSystem.IsWindows())
            return;

        foreach (var file in Directory.GetFiles(directory))
        {
            var mode = File.GetUnixFileMode(file);
            File.SetUnixFileMode(file, mode | UnixFileMode.UserExecute);
        }
    }

    string? FindSettings(string packageDirectory)
    {
        var files = Directory.GetFiles(packageDirectory, "DotnetToolSettings.xml", SearchOption.AllDirectories);
        return ToolSettingsLocator.Choose(files, hostRid, muxerPath);
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
