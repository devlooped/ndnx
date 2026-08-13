using System.IO.Compression;
using System.Net.Http.Json;
using System.Xml.Linq;

namespace ndnx;

public sealed record PackageIdentity(string Id, PackageVersion Version, string Source);

/// <summary>
/// Lists and downloads packages from a local folder feed or a NuGet v3 HTTP source.
/// </summary>
public sealed class PackageFeed
{
    readonly HttpClient http;
    readonly bool ignoreFailedSources;
    readonly TextWriter? log;

    public PackageFeed(HttpClient http, bool ignoreFailedSources, TextWriter? log = null)
    {
        this.http = http;
        this.ignoreFailedSources = ignoreFailedSources;
        this.log = log;
    }

    public async Task<IReadOnlyList<PackageIdentity>> ListAsync(
        IReadOnlyList<string> sources,
        string packageId,
        VersionRange range,
        CancellationToken cancellationToken = default)
    {
        var matches = new List<PackageIdentity>();
        Exception? lastError = null;

        foreach (var source in sources)
        {
            try
            {
                if (Directory.Exists(source))
                {
                    matches.AddRange(ListLocal(source, packageId, range));
                    continue;
                }

                matches.AddRange(await ListHttpAsync(source, packageId, range, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ignoreFailedSources)
            {
                lastError = ex;
                log?.WriteLine($"Failed to query source '{source}': {ex.Message}");
            }
        }

        if (matches.Count == 0 && lastError is not null && !sources.Any(Directory.Exists))
            throw new InvalidOperationException("All package sources failed.", lastError);

        return matches;
    }

    public async Task DownloadAsync(PackageIdentity package, string destinationNupkg, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationNupkg)!);

        if (Directory.Exists(package.Source))
        {
            var nupkg = FindLocalNupkg(package.Source, package.Id, package.Version)
                ?? throw new FileNotFoundException($"Package {package.Id} {package.Version} was not found in '{package.Source}'.");
            File.Copy(nupkg, destinationNupkg, overwrite: true);
            return;
        }

        var url = await GetPackageUrlAsync(package, cancellationToken).ConfigureAwait(false);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(destinationNupkg);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    IEnumerable<PackageIdentity> ListLocal(string directory, string packageId, VersionRange range)
    {
        foreach (var nupkg in Directory.EnumerateFiles(directory, "*.nupkg", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(nupkg).EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryReadIdentity(nupkg, out var id, out var version))
                continue;

            if (!id.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!range.Matches(version))
                continue;

            yield return new PackageIdentity(id, version, directory);
        }
    }

    static string? FindLocalNupkg(string directory, string packageId, PackageVersion version)
    {
        foreach (var nupkg in Directory.EnumerateFiles(directory, "*.nupkg", SearchOption.AllDirectories))
        {
            if (!TryReadIdentity(nupkg, out var id, out var found))
                continue;
            if (id.Equals(packageId, StringComparison.OrdinalIgnoreCase) && found.Equals(version))
                return nupkg;
        }

        return null;
    }

    async Task<IReadOnlyList<PackageIdentity>> ListHttpAsync(
        string source,
        string packageId,
        VersionRange range,
        CancellationToken cancellationToken)
    {
        var baseAddress = await GetPackageBaseAddressAsync(source, cancellationToken).ConfigureAwait(false);
        var id = packageId.ToLowerInvariant();
        var indexUrl = $"{baseAddress}{id}/index.json";
        var index = await http.GetFromJsonAsync(indexUrl, NuGetJsonContext.Default.FlatContainerIndex, cancellationToken).ConfigureAwait(false);
        if (index?.Versions is null)
            return [];

        var matches = new List<PackageIdentity>();
        foreach (var text in index.Versions)
        {
            if (!PackageVersion.TryParse(text, out var version))
                continue;
            if (!range.Matches(version))
                continue;
            matches.Add(new PackageIdentity(packageId, version, source));
        }

        return matches;
    }

    async Task<string> GetPackageUrlAsync(PackageIdentity package, CancellationToken cancellationToken)
    {
        var baseAddress = await GetPackageBaseAddressAsync(package.Source, cancellationToken).ConfigureAwait(false);
        var id = package.Id.ToLowerInvariant();
        var version = package.Version.ToString().ToLowerInvariant();
        return $"{baseAddress}{id}/{version}/{id}.{version}.nupkg";
    }

    async Task<string> GetPackageBaseAddressAsync(string source, CancellationToken cancellationToken)
    {
        if (!source.Contains("index.json", StringComparison.OrdinalIgnoreCase))
            return EnsureTrailingSlash(source);

        var index = await http.GetFromJsonAsync(source, NuGetJsonContext.Default.ServiceIndex, cancellationToken).ConfigureAwait(false);
        var resource = index?.Resources?.FirstOrDefault(r =>
            r.Type is not null && r.Type.StartsWith("PackageBaseAddress", StringComparison.OrdinalIgnoreCase));

        if (resource?.Id is null)
            throw new InvalidOperationException($"Package source '{source}' does not advertise a PackageBaseAddress.");

        return EnsureTrailingSlash(resource.Id);
    }

    static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";

    internal static bool TryReadIdentity(string nupkgPath, out string id, out PackageVersion version)
    {
        id = "";
        version = default;

        try
        {
            using var zip = ZipFile.OpenRead(nupkgPath);
            var nuspec = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
                !e.FullName.Contains('/'));
            nuspec ??= zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspec is null)
                return false;

            using var stream = nuspec.Open();
            var document = XDocument.Load(stream);
            var metadata = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            var idValue = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "id")?.Value;
            var versionValue = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "version")?.Value;
            if (idValue is null || versionValue is null || !PackageVersion.TryParse(versionValue, out version))
                return false;

            id = idValue;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
