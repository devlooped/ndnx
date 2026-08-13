using System.Diagnostics;
using ndnx;

namespace Tests;

public class ToolInvokeTests : IClassFixture<HelloToolFeed>
{
    readonly HelloToolFeed feed;

    public ToolInvokeTests(HelloToolFeed feed) => this.feed = feed;

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
