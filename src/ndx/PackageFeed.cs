using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;

namespace ndx;

public sealed record PackageIdentity(string Id, PackageVersion Version, string Source);

/// <summary>
/// Lists and downloads packages from a local folder feed or a NuGet v3 HTTP source.
/// </summary>
public sealed class PackageFeed
{
    readonly HttpClient http;
    readonly bool ignoreFailedSources;
    readonly TextWriter? log;
    readonly DownloadProgressWriter? progress;
    readonly ConcurrentDictionary<string, Task<ServiceIndex>> serviceIndexes = new(StringComparer.OrdinalIgnoreCase);

    public PackageFeed(HttpClient http, bool ignoreFailedSources, TextWriter? log = null, TextWriter? progress = null)
    {
        this.http = http;
        this.ignoreFailedSources = ignoreFailedSources;
        this.log = log;
        this.progress = progress is null ? null : new DownloadProgressWriter(progress);
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
        if (!await TryDownloadAsync(package, destinationNupkg, cancellationToken).ConfigureAwait(false))
        {
            throw new FileNotFoundException(
                $"Package {package.Id} {package.Version} was not found in '{package.Source}'.");
        }
    }

    /// <summary>
    /// Downloads <paramref name="package"/> to <paramref name="destinationNupkg"/> without listing
    /// versions. HTTP 404 (or a missing local nupkg) returns <c>false</c> so the caller can
    /// fall back to <see cref="ListAsync"/>.
    /// </summary>
    public async Task<bool> TryDownloadAsync(PackageIdentity package, string destinationNupkg, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationNupkg)!);

        if (Directory.Exists(package.Source))
        {
            var nupkg = FindLocalNupkg(package.Source, package.Id, package.Version);
            if (nupkg is null)
                return false;

            File.Copy(nupkg, destinationNupkg, overwrite: true);
            return true;
        }

        var url = await GetPackageUrlAsync(package, cancellationToken).ConfigureAwait(false);
        var catalogSize = progress is not null
            ? await TryGetCatalogPackageSizeAsync(package, cancellationToken).ConfigureAwait(false)
            : null;
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        response.EnsureSuccessStatusCode();
        var total = catalogSize ?? response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(destinationNupkg);
        await CopyAsync(input, output, total, cancellationToken).ConfigureAwait(false);
        return true;
    }

    async Task CopyAsync(Stream input, Stream output, long? total, CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var buffer = new byte[81920];
            long transferred = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                transferred += read;
                progress.Report(transferred, total);
            }
        }
        finally
        {
            progress.Complete();
        }
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
        using var response = await http.GetAsync(indexUrl, cancellationToken).ConfigureAwait(false);
        // nuget.org 404s the flat-container index when the package ID has never been
        // listed. Treat that as an empty version list so callers can report not-found
        // the way dnx does, instead of leaking EnsureSuccessStatusCode's 404.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        response.EnsureSuccessStatusCode();
        var index = await response.Content.ReadFromJsonAsync(
                NuGetJsonContext.Default.FlatContainerIndex, cancellationToken)
            .ConfigureAwait(false);
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

        var index = await GetServiceIndexAsync(source, cancellationToken).ConfigureAwait(false);
        var resource = index.Resources?.FirstOrDefault(r =>
            r.Type is not null && r.Type.StartsWith("PackageBaseAddress", StringComparison.OrdinalIgnoreCase));

        if (resource?.Id is null)
            throw new InvalidOperationException($"Package source '{source}' does not advertise a PackageBaseAddress.");

        return EnsureTrailingSlash(resource.Id);
    }

    async Task<long?> TryGetCatalogPackageSizeAsync(PackageIdentity package, CancellationToken cancellationToken)
    {
        try
        {
            if (!package.Source.Contains("index.json", StringComparison.OrdinalIgnoreCase))
                return null;

            var index = await GetServiceIndexAsync(package.Source, cancellationToken).ConfigureAwait(false);
            var registrations = PickRegistrationsBase(index);
            if (registrations is null)
                return null;

            var id = package.Id.ToLowerInvariant();
            var version = package.Version.ToString().ToLowerInvariant();
            var leafUrl = $"{registrations}{id}/{version}.json";
            using var leafResponse = await http.GetAsync(leafUrl, cancellationToken).ConfigureAwait(false);
            if (!leafResponse.IsSuccessStatusCode)
                return null;

            var leaf = await leafResponse.Content.ReadFromJsonAsync(
                    NuGetJsonContext.Default.RegistrationLeaf, cancellationToken)
                .ConfigureAwait(false);
            if (leaf is null)
                return null;

            return await ReadPackageSizeAsync(leaf.CatalogEntry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.WriteLine($"Failed to query package size for {package.Id} {package.Version}: {ex.Message}");
            return null;
        }
    }

    async Task<long?> ReadPackageSizeAsync(JsonElement catalogEntry, CancellationToken cancellationToken)
    {
        switch (catalogEntry.ValueKind)
        {
            case JsonValueKind.String:
                return await ReadCatalogLeafSizeAsync(catalogEntry.GetString(), cancellationToken).ConfigureAwait(false);
            case JsonValueKind.Object:
                if (catalogEntry.TryGetProperty("packageSize", out var inline) &&
                    inline.TryGetInt64(out var size) && size > 0)
                    return size;
                if (catalogEntry.TryGetProperty("@id", out var id) && id.GetString() is { Length: > 0 } url)
                    return await ReadCatalogLeafSizeAsync(url, cancellationToken).ConfigureAwait(false);
                return null;
            default:
                return null;
        }
    }

    async Task<long?> ReadCatalogLeafSizeAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var leaf = await response.Content.ReadFromJsonAsync(
                NuGetJsonContext.Default.CatalogLeaf, cancellationToken)
            .ConfigureAwait(false);
        return leaf?.PackageSize is > 0 ? leaf.PackageSize : null;
    }

    async Task<ServiceIndex> GetServiceIndexAsync(string source, CancellationToken cancellationToken)
    {
        var cached = serviceIndexes.GetOrAdd(source, s => FetchServiceIndexAsync(s, cancellationToken));
        try
        {
            return await cached.ConfigureAwait(false);
        }
        catch
        {
            serviceIndexes.TryRemove(KeyValuePair.Create(source, cached));
            throw;
        }
    }

    async Task<ServiceIndex> FetchServiceIndexAsync(string source, CancellationToken cancellationToken)
    {
        var index = await http.GetFromJsonAsync(source, NuGetJsonContext.Default.ServiceIndex, cancellationToken).ConfigureAwait(false);
        return index ?? throw new InvalidOperationException($"Package source '{source}' returned an empty service index.");
    }

    static string? PickRegistrationsBase(ServiceIndex index)
    {
        var matches = index.Resources?
            .Where(r => r.Id is not null && r.Type is not null &&
                r.Type.StartsWith("RegistrationsBaseUrl", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches is not { Count: > 0 })
            return null;

        var preferred = matches.FirstOrDefault(r =>
                r.Type!.Equals("RegistrationsBaseUrl/3.6.0", StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault(r =>
                r.Type!.Equals("RegistrationsBaseUrl/3.4.0", StringComparison.OrdinalIgnoreCase))
            ?? matches[0];
        return EnsureTrailingSlash(preferred.Id!);
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
