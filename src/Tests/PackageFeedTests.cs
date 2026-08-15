using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using ndnx;

namespace Tests;

public class PackageFeedTests
{
    const string Source = "https://feed.test/index.json";
    const string Flat = "https://feed.test/flat/";
    const string PackageId = "hello-tool";
    const string RidWrapperId = "hello-rid";
    const string VersionText = "1.0.0";

    static readonly string HostRid = RuntimeInformation.RuntimeIdentifier;
    static readonly string RidImplId = $"{RidWrapperId}.{HostRid}";
    static readonly PackageVersion Version = ParseVersion();

    [Fact]
    public async Task Service_index_is_fetched_once_per_source()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        using var http = new HttpClient(handler);
        var packageFeed = new PackageFeed(http, ignoreFailedSources: false);
        var dest1 = Path.Combine(packages.Root, "a.nupkg");
        var dest2 = Path.Combine(packages.Root, "b.nupkg");
        var identity = new PackageIdentity(PackageId, Version, Source);

        Assert.True(await packageFeed.TryDownloadAsync(identity, dest1));
        Assert.True(await packageFeed.TryDownloadAsync(identity, dest2));
        await packageFeed.ListAsync([Source], PackageId, VersionRange.Exact(Version));

        Assert.Equal(1, handler.Hits.Count(url => url == Source));
        Assert.Equal(2, handler.Hits.Count(url => url == NupkgUrl(PackageId)));
        Assert.Equal(1, handler.Hits.Count(url => url == VersionsUrl(PackageId)));
    }

    [Fact]
    public async Task Exact_version_downloads_nupkg_without_listing()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        var command = await GetExactAsync(handler, PackageId);

        Assert.True(File.Exists(command.EntryPointPath), command.EntryPointPath);
        Assert.Equal(1, handler.Hits.Count(url => url == Source));
        Assert.Contains(NupkgUrl(PackageId), handler.Hits);
        Assert.DoesNotContain(VersionsUrl(PackageId), handler.Hits);
    }

    [Fact]
    public async Task Exact_rid_hop_downloads_without_listing_either_package()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        handler.Map[NupkgUrl(RidWrapperId)] = File.ReadAllBytes(packages.RidWrapper);
        handler.Map[NupkgUrl(RidImplId)] = File.ReadAllBytes(packages.RidImpl);
        handler.Map[VersionsUrl(RidWrapperId)] = VersionsJson();
        handler.Map[VersionsUrl(RidImplId)] = VersionsJson();

        var command = await GetExactAsync(handler, RidWrapperId);

        Assert.Contains(RidImplId, command.EntryPointPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.Hits.Count(url => url == Source));
        Assert.Contains(NupkgUrl(RidWrapperId), handler.Hits);
        Assert.Contains(NupkgUrl(RidImplId), handler.Hits);
        Assert.DoesNotContain(VersionsUrl(RidWrapperId), handler.Hits);
        Assert.DoesNotContain(VersionsUrl(RidImplId), handler.Hits);
    }

    [Fact]
    public async Task Exact_nupkg_404_falls_back_to_list()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        var nupkg = NupkgUrl(PackageId);
        var remaining404 = 1;
        handler.Override[nupkg] = () =>
        {
            if (remaining404-- > 0)
                return (HttpStatusCode.NotFound, "not found"u8.ToArray());
            return (HttpStatusCode.OK, File.ReadAllBytes(packages.Plain));
        };

        var command = await GetExactAsync(handler, PackageId);

        Assert.True(File.Exists(command.EntryPointPath), command.EntryPointPath);
        Assert.Equal(2, handler.Hits.Count(url => url == nupkg));
        Assert.Contains(VersionsUrl(PackageId), handler.Hits);
    }

    [Fact]
    public async Task Exact_nupkg_404_and_empty_list_is_not_found()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        handler.Map[NupkgUrl(PackageId)] = [];
        handler.Status[NupkgUrl(PackageId)] = HttpStatusCode.NotFound;
        handler.Map[VersionsUrl(PackageId)] = """{"versions":[]}"""u8.ToArray();

        using var http = new HttpClient(handler);
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), NewStore());
        var invocation = ArgParser.Parse(PackageId + "@" + VersionText, "--yes", "--source", Source);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync(invocation, [Source]));
        Assert.Contains("was not found", error.Message);
        Assert.Contains(VersionsUrl(PackageId), handler.Hits);
    }

    static async Task<ToolCommand> GetExactAsync(RecordingHandler handler, string packageId)
    {
        using var http = new HttpClient(handler);
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), NewStore());
        var invocation = ArgParser.Parse(packageId + "@" + VersionText, "--yes", "--source", Source);
        return await store.GetAsync(invocation, [Source]);
    }

    static RecordingHandler MapFeed(TempNupkgs packages)
    {
        var handler = new RecordingHandler();
        handler.Map[Source] = """
            {"resources":[{"@id":"https://feed.test/flat/","@type":"PackageBaseAddress/3.0.0"}]}
            """u8.ToArray();
        handler.Map[NupkgUrl(PackageId)] = File.ReadAllBytes(packages.Plain);
        handler.Map[VersionsUrl(PackageId)] = VersionsJson();
        return handler;
    }

    static byte[] VersionsJson() => """{"versions":["1.0.0"]}"""u8.ToArray();

    static string NupkgUrl(string packageId)
    {
        var id = packageId.ToLowerInvariant();
        return $"{Flat}{id}/{VersionText}/{id}.{VersionText}.nupkg";
    }

    static string VersionsUrl(string packageId)
        => $"{Flat}{packageId.ToLowerInvariant()}/index.json";

    static PackageVersion ParseVersion()
        => PackageVersion.TryParse(VersionText, out var parsed)
            ? parsed
            : throw new InvalidOperationException("bad version");

    static string NewStore()
    {
        var store = Path.Combine(Path.GetTempPath(), "ndnx-feed-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(store);
        return store;
    }

    sealed class TempNupkgs : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ndnx-feed-nupkgs", Guid.NewGuid().ToString("n"));
        public string Plain { get; }
        public string RidWrapper { get; }
        public string RidImpl { get; }

        public TempNupkgs()
        {
            Directory.CreateDirectory(Root);
            Plain = WriteTool(PackageId, "hello-tool", ridPackages: null);
            RidWrapper = WriteTool(RidWrapperId, "hello-rid", ridPackages: (HostRid, RidImplId));
            RidImpl = WriteTool(RidImplId, "hello-rid", ridPackages: null);
        }

        string WriteTool(string packageId, string commandName, (string Rid, string Id)? ridPackages)
        {
            var staging = Path.Combine(Root, "stg-" + packageId);
            var tools = Path.Combine(staging, "tools", "net10.0", "any");
            Directory.CreateDirectory(tools);
            File.WriteAllText(Path.Combine(tools, "tool.bin"), "ok");

            var ridXml = ridPackages is { } map
                ? $"""
                      <RuntimeIdentifierPackages>
                        <RuntimeIdentifierPackage RuntimeIdentifier="{map.Rid}" Id="{map.Id}" />
                      </RuntimeIdentifierPackages>
                    """
                : "";
            var runner = ridPackages is null ? "executable" : "";
            var entry = ridPackages is null ? " EntryPoint=\"tool.bin\" Runner=\"executable\"" : "";
            File.WriteAllText(Path.Combine(tools, "DotnetToolSettings.xml"),
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <DotNetCliTool Version="1">
                  <Commands>
                    <Command Name="{commandName}"{entry} />
                  </Commands>
                {ridXml}
                </DotNetCliTool>
                """);

            File.WriteAllText(Path.Combine(staging, packageId + ".nuspec"),
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{packageId}</id>
                    <version>{VersionText}</version>
                    <authors>ndnx</authors>
                    <description>{packageId}</description>
                    <packageTypes>
                      <packageType name="{(ridPackages is null && packageId != RidImplId ? "DotnetTool" : packageId == RidWrapperId ? "DotnetTool" : "DotnetToolRidPackage")}" />
                    </packageTypes>
                  </metadata>
                </package>
                """);

            var nupkg = Path.Combine(Root, $"{packageId}.{VersionText}.nupkg");
            ZipFile.CreateFromDirectory(staging, nupkg);
            return nupkg;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }

    sealed class RecordingHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Map { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HttpStatusCode> Status { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Func<(HttpStatusCode Status, byte[] Body)>> Override { get; } = new(StringComparer.Ordinal);
        public List<string> Hits { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            Hits.Add(url);

            if (Override.TryGetValue(url, out var factory))
            {
                var produced = factory();
                return Task.FromResult(Response(request, produced.Status, produced.Body));
            }

            if (!Map.TryGetValue(url, out var body))
                return Task.FromResult(Response(request, HttpStatusCode.NotFound, "not found"u8.ToArray()));

            var status = Status.TryGetValue(url, out var mapped) ? mapped : HttpStatusCode.OK;
            return Task.FromResult(Response(request, status, body));
        }

        static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status, byte[] body)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request,
            };
            var nupkg = request.RequestUri?.AbsolutePath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) == true;
            response.Content.Headers.ContentType = new(nupkg ? "application/octet-stream" : "application/json");
            return response;
        }
    }
}
