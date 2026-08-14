using System.Net;
using System.Text;
using ndnx;

namespace Tests;

public class SelfUpdateTests
{
    const string Repo = "devlooped/ndnx";
    const string Rid = "win-x64";

    [Fact]
    public async Task Update_to_latest_replaces_the_binary_and_prints_the_target_version()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndnx.exe");
        File.WriteAllBytes(current, "old-binary"u8.ToArray());

        using var handler = Feed(dir, latest: "0.2.0", payload: "new-binary"u8.ToArray());
        var host = NewHost(dir, current, "0.1.0", handler);

        var code = await App.RunAsync(["--update"], host);

        Assert.Equal(0, code);
        Assert.Equal("new-binary"u8.ToArray(), File.ReadAllBytes(current));
        Assert.Contains("Updating to 0.2.0", host.Out.ToString());
        Assert.Contains("updated", host.Out.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(current, host.Out.ToString());
    }

    [Fact]
    public async Task Update_skips_download_when_already_on_latest()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndnx.exe");
        var payload = "same-binary"u8.ToArray();
        File.WriteAllBytes(current, payload);

        using var handler = Feed(dir, latest: "0.2.0", payload: "unused"u8.ToArray());
        var host = NewHost(dir, current, "0.2.0", handler);

        var code = await App.RunAsync(["--update"], host);

        Assert.Equal(0, code);
        Assert.Equal(payload, File.ReadAllBytes(current));
        Assert.Contains("already 0.2.0", host.Out.ToString());
        Assert.Equal(1, handler.Hits.Count(u => u.Contains("/releases/latest", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Hits, u => u.Contains("/releases/download/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_to_an_older_version_is_allowed()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndnx.exe");
        File.WriteAllBytes(current, "newer-binary"u8.ToArray());

        using var handler = Feed(dir, latest: "0.2.0", payload: "older-binary"u8.ToArray(), extraVersion: "0.1.0");
        var host = NewHost(dir, current, "0.2.0", handler);

        var code = await App.RunAsync(["--update", "0.1.0"], host);

        Assert.Equal(0, code);
        Assert.Equal("older-binary"u8.ToArray(), File.ReadAllBytes(current));
        Assert.Contains("Updating to 0.1.0", host.Out.ToString());
        Assert.DoesNotContain(handler.Hits, u => u.Contains("/releases/latest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_to_a_missing_version_fails()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndnx.exe");
        File.WriteAllBytes(current, "old-binary"u8.ToArray());

        using var handler = Feed(dir, latest: "0.2.0", payload: "new-binary"u8.ToArray());
        var host = NewHost(dir, current, "0.1.0", handler);

        var code = await App.RunAsync(["--update", "9.9.9"], host);

        Assert.Equal(1, code);
        Assert.Equal("old-binary"u8.ToArray(), File.ReadAllBytes(current));
        Assert.Contains("9.9.9", host.Error.ToString());
        Assert.Contains("404", host.Error.ToString());
    }

    [Fact]
    public async Task Update_extracts_a_unix_targz_archive()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndnx");
        File.WriteAllBytes(current, "old-unix"u8.ToArray());

        const string unixRid = "linux-x64";
        using var handler = new MapHandler();
        var publish = Path.Combine(dir.Root, "publish-unix");
        Directory.CreateDirectory(publish);
        File.WriteAllBytes(Path.Combine(publish, "ndnx"), "new-unix"u8.ToArray());
        var packed = NativePacker.Pack(publish, unixRid, Path.Combine(dir.Root, "out-unix"), "0.3.0");
        var name = Path.GetFileName(packed.ArchivePath);
        handler.Map[SelfUpdate.AssetUrl(Repo, "v0.3.0", name)] =
            (HttpStatusCode.OK, File.ReadAllBytes(packed.ArchivePath), "application/octet-stream");
        handler.Map[SelfUpdate.AssetUrl(Repo, "v0.3.0", name) + ".sha256"] =
            (HttpStatusCode.OK, File.ReadAllBytes(packed.Sha256Path), "text/plain");

        var host = new NdnxHost
        {
            WorkingDirectory = dir.Root,
            StoreDirectory = dir.Store,
            ProcessRunner = new RecordingProcessRunner(),
            Out = new StringWriter(),
            Error = new StringWriter(),
            HttpHandler = handler,
            ExecutablePath = current,
            CurrentVersion = "0.1.0",
            UpdateRepository = Repo,
            RuntimeIdentifier = unixRid,
        };
        var code = await App.RunAsync(["--update", "0.3.0"], host);

        Assert.Equal(0, code);
        Assert.Equal("new-unix"u8.ToArray(), File.ReadAllBytes(current));
        Assert.Contains("Updating to 0.3.0", host.Out.ToString());
    }

    [Fact]
    public async Task Update_does_not_launch_a_child()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndnx.exe");
        File.WriteAllBytes(current, "old-binary"u8.ToArray());

        using var handler = Feed(dir, latest: "0.2.0", payload: "new-binary"u8.ToArray());
        var runner = new RecordingProcessRunner();
        var host = NewHost(dir, current, "0.1.0", handler, runner);

        var code = await App.RunAsync(["--update"], host);

        Assert.Equal(0, code);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Sha256_mismatch_leaves_the_current_binary()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndnx.exe");
        File.WriteAllBytes(current, "old-binary"u8.ToArray());

        using var handler = Feed(dir, latest: "0.2.0", payload: "new-binary"u8.ToArray());
        var archive = SelfUpdate.ArchiveFileName(Rid, "0.2.0");
        handler.Map[SelfUpdate.AssetUrl(Repo, "v0.2.0", archive) + ".sha256"] =
            (HttpStatusCode.OK, "0"u8.ToArray(), "text/plain");
        var host = NewHost(dir, current, "0.1.0", handler);

        var code = await App.RunAsync(["--update"], host);

        Assert.Equal(1, code);
        Assert.Equal("old-binary"u8.ToArray(), File.ReadAllBytes(current));
        Assert.Contains("SHA256", host.Error.ToString());
    }

    static NdnxHost NewHost(TempDir dir, string executable, string currentVersion, MapHandler handler, IProcessRunner? runner = null)
        => new()
        {
            WorkingDirectory = dir.Root,
            StoreDirectory = dir.Store,
            ProcessRunner = runner ?? new RecordingProcessRunner(),
            Out = new StringWriter(),
            Error = new StringWriter(),
            HttpHandler = handler,
            ExecutablePath = executable,
            CurrentVersion = currentVersion,
            UpdateRepository = Repo,
            RuntimeIdentifier = Rid,
        };

    static MapHandler Feed(
        TempDir dir,
        string latest,
        byte[] payload,
        string? extraVersion = null)
    {
        var handler = new MapHandler();
        handler.Map[SelfUpdate.LatestReleaseUrl(Repo)] =
            (HttpStatusCode.OK, Encoding.UTF8.GetBytes($$"""{"tag_name":"v{{latest}}"}"""), "application/json");

        AddRelease(handler, dir, latest, payload);
        if (extraVersion is not null)
            AddRelease(handler, dir, extraVersion, payload);

        return handler;
    }

    static void AddRelease(MapHandler handler, TempDir dir, string version, byte[] payload)
    {
        var publish = Path.Combine(dir.Root, "publish-" + version);
        Directory.CreateDirectory(publish);
        File.WriteAllBytes(Path.Combine(publish, "ndnx.exe"), payload);
        var packed = NativePacker.Pack(publish, Rid, Path.Combine(dir.Root, "out-" + version), version);
        var archive = File.ReadAllBytes(packed.ArchivePath);
        var sha = File.ReadAllBytes(packed.Sha256Path);
        var name = Path.GetFileName(packed.ArchivePath);
        handler.Map[SelfUpdate.AssetUrl(Repo, "v" + version, name)] = (HttpStatusCode.OK, archive, "application/octet-stream");
        handler.Map[SelfUpdate.AssetUrl(Repo, "v" + version, name) + ".sha256"] = (HttpStatusCode.OK, sha, "text/plain");
    }

    sealed class MapHandler : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, byte[] Body, string Media)> Map { get; } = new(StringComparer.Ordinal);
        public List<string> Hits { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            Hits.Add(url);
            if (!Map.TryGetValue(url, out var mapped))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("not found"),
                    RequestMessage = request,
                });
            }

            var response = new HttpResponseMessage(mapped.Status)
            {
                Content = new ByteArrayContent(mapped.Body),
                RequestMessage = request,
            };
            response.Content.Headers.ContentType = new(mapped.Media);
            return Task.FromResult(response);
        }
    }

    sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ndnx-update-tests", Guid.NewGuid().ToString("n"));
        public string Prefix => Path.Combine(Root, "prefix");
        public string Store => Path.Combine(Root, "store");

        public TempDir()
        {
            Directory.CreateDirectory(Prefix);
            Directory.CreateDirectory(Store);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
