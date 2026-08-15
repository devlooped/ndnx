using System.Diagnostics;
using ndnx;

namespace Tests;

public class ToolInvokeTests : IClassFixture<HelloToolFeed>
{
    readonly HelloToolFeed feed;

    public ToolInvokeTests(HelloToolFeed feed) => this.feed = feed;

    [Fact]
    public async Task Version_alone_prints_ndnx_version_and_does_not_launch()
    {
        var runner = new RecordingProcessRunner();
        var host = new NdnxHost
        {
            WorkingDirectory = feed.Root,
            StoreDirectory = NewStore(),
            ProcessRunner = runner,
            Out = new StringWriter(),
            Error = new StringWriter(),
            CurrentVersion = "0.2.0",
        };

        var code = await App.RunAsync(["--version"], host);

        Assert.Equal(0, code);
        Assert.Equal(0, runner.Calls);
        Assert.Equal("0.2.0" + Environment.NewLine, host.Out.ToString());
        Assert.Equal("", host.Error.ToString());
    }

    [Fact]
    public async Task Missing_package_operand_does_not_launch_a_child()
    {
        var runner = new RecordingProcessRunner();
        var host = NewHost(runner);

        var code = await App.RunAsync(["--yes", "--source", feed.FeedDirectory], host);

        Assert.Equal(1, code);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("PACKAGE_NAME", host.Error.ToString());
    }

    [Fact]
    public async Task First_run_downloads_and_second_run_uses_cache()
    {
        var store = NewStore();
        var host = NewHost(new ProcessRunner(), store);

        Assert.False(Cached(store, HelloToolFeed.PackageId));

        var first = await App.RunAsync(InvokeArgs(HelloToolFeed.PackageId), host);
        Assert.Equal(0, first);
        Assert.True(Cached(store, HelloToolFeed.PackageId));
        var markerTime = File.GetLastWriteTimeUtc(Marker(store, HelloToolFeed.PackageId));

        var second = await App.RunAsync(InvokeArgs(HelloToolFeed.PackageId), host);
        Assert.Equal(0, second);
        Assert.True(Cached(store, HelloToolFeed.PackageId));
        Assert.Equal(markerTime, File.GetLastWriteTimeUtc(Marker(store, HelloToolFeed.PackageId)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Relays_child_exit_code(int expected)
    {
        var host = NewHost(new ProcessRunner());
        var code = await App.RunAsync(InvokeArgs(HelloToolFeed.PackageId, expected.ToString()), host);
        Assert.Equal(expected, code);
    }

    [Fact]
    public async Task Non_aot_framework_dependent_fixture_runs_on_the_same_path()
    {
        var host = NewHost(new ProcessRunner());
        var code = await App.RunAsync(InvokeArgs(HelloToolFeed.PackageId, "0", "from-fdd"), host);
        Assert.Equal(0, code);
        Assert.True(Cached(host.StoreDirectory, HelloToolFeed.PackageId));
    }

    [Fact]
    public async Task Executable_runner_fixture_runs_the_same_way()
    {
        var host = NewHost(new ProcessRunner());
        var code = await App.RunAsync(InvokeArgs(HelloToolFeed.ExePackageId, "0", "from-exe"), host);
        Assert.Equal(0, code);
        Assert.True(Cached(host.StoreDirectory, HelloToolFeed.ExePackageId));
    }

    [Fact]
    public void Real_entry_point_writes_fixture_stdout_on_ndnx_stdout()
    {
        var store = NewStore();
        var result = LaunchNdnx(
            store,
            [HelloToolFeed.PackageId + "@" + HelloToolFeed.PackageVersion, "--yes", "--source", feed.FeedDirectory, "--", "0", "via-entry"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(HelloToolFeed.Phrase, result.Stdout);
        Assert.Contains("arg:0", result.Stdout);
        Assert.Contains("arg:via-entry", result.Stdout);
    }

    [Fact]
    public void Real_entry_point_relays_nonzero_exit_and_stdout()
    {
        var store = NewStore();
        var result = LaunchNdnx(
            store,
            [HelloToolFeed.PackageId + "@" + HelloToolFeed.PackageVersion, "--yes", "--source", feed.FeedDirectory, "--", "11"]);

        Assert.Equal(11, result.ExitCode);
        Assert.Contains(HelloToolFeed.Phrase, result.Stdout);
    }

    [Fact]
    public async Task Multi_rid_store_get_uses_rid_package_entry_point()
    {
        var storeDir = NewStore();
        var command = await GetShippedAsync(storeDir, HelloToolFeed.RidWrapperId);

        var ridDir = Path.GetFullPath(new ToolPackageStore(NullFeed(), storeDir)
            .GetPackageDirectory(HelloToolFeed.RidImplId, ParsedFixtureVersion()));
        Assert.StartsWith(ridDir, Path.GetFullPath(command.EntryPointPath), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(command.EntryPointPath));
        Assert.Equal("dotnet", command.Runner);
        Assert.Equal("hello-rid", command.Name);

        var host = NewHost(new ProcessRunner(), storeDir);
        var code = await App.RunAsync(InvokeArgs(HelloToolFeed.RidWrapperId, "0", "from-rid"), host);
        Assert.Equal(0, code);
        Assert.True(Cached(storeDir, HelloToolFeed.RidImplId));
    }

    [Fact]
    public async Task Multi_rid_any_fallback_uses_any_package_not_wrapper()
    {
        var storeDir = NewStore();
        var command = await GetShippedAsync(storeDir, HelloToolFeed.AnyWrapperId);

        var anyDir = Path.GetFullPath(new ToolPackageStore(NullFeed(), storeDir)
            .GetPackageDirectory(HelloToolFeed.AnyImplId, ParsedFixtureVersion()));
        var wrapperDir = Path.GetFullPath(new ToolPackageStore(NullFeed(), storeDir)
            .GetPackageDirectory(HelloToolFeed.AnyWrapperId, ParsedFixtureVersion()));
        Assert.StartsWith(anyDir, Path.GetFullPath(command.EntryPointPath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.DirectorySeparatorChar + HelloToolFeed.AnyWrapperId + Path.DirectorySeparatorChar,
            command.EntryPointPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(wrapperDir, "tools", "any", "any", "DotnetToolSettings.xml")));

        var result = LaunchNdnx(
            storeDir,
            [HelloToolFeed.AnyWrapperId + "@" + HelloToolFeed.PackageVersion, "--yes", "--source", feed.FeedDirectory, "--", "0", "from-any"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(HelloToolFeed.AnyPhrase, result.Stdout);
        Assert.DoesNotContain("declares RID-specific packages", result.Stdout);
        Assert.DoesNotContain("declares RID-specific packages", result.Stderr);
        Assert.DoesNotContain("not resolved by this runner", result.Stdout + result.Stderr);
    }

    [Fact]
    public async Task Multi_rid_no_match_names_host_and_declared_rids()
    {
        var storeDir = NewStore();
        using var http = new HttpClient();
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), storeDir);
        var invocation = ArgParser.Parse(
            HelloToolFeed.NoMatchWrapperId + "@" + HelloToolFeed.PackageVersion,
            "--yes",
            "--source",
            feed.FeedDirectory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync(invocation, [feed.FeedDirectory]));

        Assert.Contains(HelloToolFeed.HostRid, ex.Message);
        Assert.Contains(HelloToolFeed.UnusedRid, ex.Message);
        Assert.Contains("Declared RIDs", ex.Message);
        Assert.DoesNotContain("not resolved by this runner", ex.Message);
    }

    [Fact]
    public void Multi_rid_real_entry_prints_rid_phrase_and_caches()
    {
        var store = NewStore();
        var args = new[]
        {
            HelloToolFeed.RidWrapperId + "@" + HelloToolFeed.PackageVersion,
            "--yes",
            "--source",
            feed.FeedDirectory,
            "--",
            "0",
            "via-multirid",
        };

        var first = LaunchNdnx(store, args);
        Assert.Equal(0, first.ExitCode);
        Assert.Contains(HelloToolFeed.Phrase, first.Stdout);
        Assert.DoesNotContain("declares RID-specific packages", first.Stdout + first.Stderr);
        Assert.True(Cached(store, HelloToolFeed.RidImplId));
        var markerTime = File.GetLastWriteTimeUtc(Marker(store, HelloToolFeed.RidImplId));

        var second = LaunchNdnx(store, args);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains(HelloToolFeed.Phrase, second.Stdout);
        Assert.Equal(markerTime, File.GetLastWriteTimeUtc(Marker(store, HelloToolFeed.RidImplId)));
    }

    [Theory]
    [InlineData("hello-tool@1.0.0")]
    [InlineData("hello-tool", "--version", "1.0.0")]
    public async Task Exact_cached_version_skips_the_feed(params string[] identity)
    {
        var storeDir = NewStore();
        await GetShippedAsync(storeDir, HelloToolFeed.PackageId);

        var command = await GetWithThrowingFeedAsync(storeDir, [.. identity, "--yes", "--source", HttpFeed]);
        Assert.True(File.Exists(command.EntryPointPath), command.EntryPointPath);
    }

    [Fact]
    public async Task Exact_cached_rid_package_skips_the_feed()
    {
        var storeDir = NewStore();
        await GetShippedAsync(storeDir, HelloToolFeed.RidWrapperId);

        var command = await GetWithThrowingFeedAsync(
            storeDir,
            [HelloToolFeed.RidWrapperId + "@" + HelloToolFeed.PackageVersion, "--yes", "--source", HttpFeed]);
        Assert.True(File.Exists(command.EntryPointPath), command.EntryPointPath);
        Assert.Contains(
            HelloToolFeed.RidImplId,
            command.EntryPointPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("hello-tool")]
    [InlineData("hello-tool@*")]
    public async Task Latest_or_star_queries_the_feed_even_when_a_version_is_cached(string identity)
    {
        var storeDir = NewStore();
        await GetShippedAsync(storeDir, HelloToolFeed.PackageId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetWithThrowingFeedAsync(storeDir, [identity, "--yes", "--source", HttpFeed]));
        Assert.Contains("Unexpected HTTP", error.Message);
    }

    [Fact]
    public async Task Exact_version_that_is_not_installed_queries_the_feed()
    {
        var storeDir = NewStore();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetWithThrowingFeedAsync(
                storeDir,
                [HelloToolFeed.PackageId + "@" + HelloToolFeed.PackageVersion, "--yes", "--source", HttpFeed]));
        Assert.Contains("Unexpected HTTP", error.Message);
    }

    NdnxHost NewHost(IProcessRunner runner, string? store = null)
    {
        store ??= NewStore();
        return new NdnxHost
        {
            WorkingDirectory = feed.Root,
            StoreDirectory = store,
            ProcessRunner = runner,
            Out = new StringWriter(),
            Error = new StringWriter(),
        };
    }

    string[] InvokeArgs(string packageId, params string[] forwarded)
    {
        var args = new List<string>
        {
            $"{packageId}@{HelloToolFeed.PackageVersion}",
            "--yes",
            "--source",
            feed.FeedDirectory,
        };
        if (forwarded.Length > 0)
        {
            args.Add("--");
            args.AddRange(forwarded);
        }

        return [.. args];
    }

    const string HttpFeed = "https://example.invalid/index.json";

    async Task<ToolCommand> GetShippedAsync(string storeDir, string packageId)
    {
        using var http = new HttpClient();
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), storeDir);
        var invocation = ArgParser.Parse(
            packageId + "@" + HelloToolFeed.PackageVersion,
            "--yes",
            "--source",
            feed.FeedDirectory);
        return await store.GetAsync(invocation, [feed.FeedDirectory]);
    }

    static async Task<ToolCommand> GetWithThrowingFeedAsync(string storeDir, string[] args)
    {
        using var http = new HttpClient(new ThrowingHandler());
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), storeDir);
        var invocation = ArgParser.Parse(args);
        Assert.True(invocation.Success, invocation.Error);
        return await store.GetAsync(invocation, [HttpFeed]);
    }

    static PackageFeed NullFeed() => new(new HttpClient(), ignoreFailedSources: false);

    sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException($"Unexpected HTTP {request.Method} {request.RequestUri}");
    }

    static PackageVersion ParsedFixtureVersion()
        => PackageVersion.TryParse(HelloToolFeed.PackageVersion, out var parsed)
            ? parsed
            : throw new InvalidOperationException("bad fixture version");

    static string NewStore()
    {
        var store = Path.Combine(Path.GetTempPath(), "ndnx-test-store", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(store);
        return store;
    }

    static bool Cached(string store, string packageId)
    {
        var version = PackageVersion.TryParse(HelloToolFeed.PackageVersion, out var parsed)
            ? parsed
            : throw new InvalidOperationException("bad fixture version");
        return ToolPackageStore.IsCached(Path.Combine(store, packageId.ToLowerInvariant(), version.ToString().ToLowerInvariant()));
    }

    static string Marker(string store, string packageId)
    {
        var version = HelloToolFeed.PackageVersion.ToLowerInvariant();
        return Path.Combine(store, packageId.ToLowerInvariant(), version, ToolPackageStore.ReadyMarker);
    }

    static (int ExitCode, string Stdout, string Stderr) LaunchNdnx(string store, string[] args)
    {
        var ndnxDll = Path.Combine(AppContext.BaseDirectory, "ndnx.dll");
        Assert.True(File.Exists(ndnxDll), $"Expected shipped ndnx.dll at {ndnxDll}");

        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(ndnxDll);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["NDNX_STORE"] = store;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start ndnx.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }
}
