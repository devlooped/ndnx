using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using ndnx;

namespace Tests;

public class EvergreenTests : IClassFixture<HelloToolFeed>
{
    const string CiFeed = "https://kzu.blob.core.windows.net/nuget/index.json";

    readonly HelloToolFeed feed;

    public EvergreenTests(HelloToolFeed feed) => this.feed = feed;

    [Fact]
    public async Task Star_version_that_exits_returns_child_code_and_does_not_restart()
    {
        var runner = new RecordingProcessRunner { ExitCode = 7 };
        var host = NewHost(runner);

        var code = await App.RunAsync(
            [HelloToolFeed.PackageId + "@*", "--yes", "--source", IsolatedHelloFeed()],
            host);

        Assert.Equal(7, code);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Unspecified_version_is_evergreen_and_uses_start()
    {
        var runner = new RecordingProcessRunner { ExitCode = 0 };
        var host = NewHost(runner);

        var code = await App.RunAsync(
            [HelloToolFeed.PackageId, "--yes", "--source", IsolatedHelloFeed()],
            host);

        Assert.Equal(0, code);
        Assert.Equal(1, runner.Calls);
        Assert.NotNull(runner.Last);
    }

    [Fact]
    public async Task Exact_version_still_uses_run()
    {
        var runner = new RecordingProcessRunner { ExitCode = 4 };
        var host = NewHost(runner);

        var code = await App.RunAsync(
            [HelloToolFeed.PackageId + "@" + HelloToolFeed.PackageVersion, "--yes", "--source", IsolatedHelloFeed()],
            host);

        Assert.Equal(4, code);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Stages_newer_package_then_stops_and_restarts()
    {
        var feedDir = IsolatedHelloFeed();
        var first = new FakeChildProcess(0, exited: false);
        var second = new FakeChildProcess(0, exited: false);
        var runner = new RecordingProcessRunner();
        runner.Next.Enqueue(first);
        runner.Next.Enqueue(second);

        var host = NewHost(runner, feedDir);
        host = new NdnxHost
        {
            WorkingDirectory = host.WorkingDirectory,
            StoreDirectory = host.StoreDirectory,
            ProcessRunner = runner,
            Out = host.Out,
            Error = host.Error,
            UpdateInterval = TimeSpan.FromMilliseconds(80),
            StopTimeout = TimeSpan.FromSeconds(2),
        };

        var run = App.RunAsync(
            [HelloToolFeed.PackageId + "@*", "--yes", "--source", feedDir],
            host);

        await WaitUntil(() => runner.Calls >= 1, TimeSpan.FromSeconds(10));
        AddPackageVersion(feedDir, "1.0.1");

        await WaitUntil(() => first.StopCalled && runner.Calls >= 2, TimeSpan.FromSeconds(15));
        Assert.True(first.StopCalled);
        Assert.Contains("Updating hello-tool 1.0.0 → 1.0.1", host.Out.ToString());
        Assert.Contains("1.0.1", Combined(runner.Starts[1]), StringComparison.OrdinalIgnoreCase);

        second.Exit(0);
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task Unchanged_feed_does_not_stop_the_running_child()
    {
        var first = new FakeChildProcess(0, exited: false);
        var runner = new RecordingProcessRunner();
        runner.Next.Enqueue(first);
        var host = NewHost(runner);
        host = new NdnxHost
        {
            WorkingDirectory = host.WorkingDirectory,
            StoreDirectory = host.StoreDirectory,
            ProcessRunner = runner,
            Out = host.Out,
            Error = host.Error,
            UpdateInterval = TimeSpan.FromMilliseconds(50),
        };

        using var cts = new CancellationTokenSource();
        var run = App.RunAsync(
            [HelloToolFeed.PackageId + "@*", "--yes", "--source", IsolatedHelloFeed()],
            host,
            cts.Token);

        await WaitUntil(() => runner.Calls >= 1, TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        Assert.False(first.StopCalled);
        Assert.Equal(1, runner.Calls);

        cts.Cancel();
        await run;
        Assert.True(first.StopCalled);
    }

    [Fact]
    public async Task Host_update_interval_overrides_netconfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ndnx-evergreen-cfg", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".netconfig"),
            """
            [ndnx]
                interval = 30
            """);

        var interval = NetConfig.ReadUpdateInterval(dir, userProfile: Path.Combine(dir, "nouser"));
        Assert.Equal(TimeSpan.FromSeconds(30), interval);

        var runner = new RecordingProcessRunner { ExitCode = 0 };
        var host = new NdnxHost
        {
            WorkingDirectory = dir,
            StoreDirectory = Path.Combine(dir, "store"),
            ProcessRunner = runner,
            Out = new StringWriter(),
            Error = new StringWriter(),
            UpdateInterval = TimeSpan.FromMilliseconds(10),
        };

        var code = await App.RunAsync(
            [HelloToolFeed.PackageId + "@*", "--yes", "--source", IsolatedHelloFeed()],
            host);
        Assert.Equal(0, code);
    }

    [Fact]
    public void Ci_feed_stop_star_help_exits()
    {
        var store = Path.Combine(Path.GetTempPath(), "ndnx-evergreen-ci", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(store);
        var result = LaunchNdnx(store, ["stop@*", "--yes", "--source", CiFeed, "--", "--help"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("timeout", result.Stdout + result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    NdnxHost NewHost(IProcessRunner runner, string? sourceDir = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ndnx-evergreen", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return new NdnxHost
        {
            WorkingDirectory = sourceDir ?? root,
            StoreDirectory = Path.Combine(root, "store"),
            ProcessRunner = runner,
            Out = new StringWriter(),
            Error = new StringWriter(),
            UpdateInterval = TimeSpan.FromHours(1),
        };
    }

    string IsolatedHelloFeed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ndnx-evergreen-feed", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var nupkg = Path.Combine(feed.FeedDirectory, $"{HelloToolFeed.PackageId}.{HelloToolFeed.PackageVersion}.nupkg");
        File.Copy(nupkg, Path.Combine(dir, Path.GetFileName(nupkg)));
        return dir;
    }

    static void AddPackageVersion(string feedDir, string version)
    {
        var source = Path.Combine(feedDir, $"{HelloToolFeed.PackageId}.{HelloToolFeed.PackageVersion}.nupkg");
        var dest = Path.Combine(feedDir, $"{HelloToolFeed.PackageId}.{version}.nupkg");
        using var input = ZipFile.OpenRead(source);
        if (File.Exists(dest))
            File.Delete(dest);
        using var output = ZipFile.Open(dest, ZipArchiveMode.Create);
        foreach (var entry in input.Entries)
        {
            var created = output.CreateEntry(entry.FullName);
            using var from = entry.Open();
            using var to = created.Open();
            if (entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            {
                var document = XDocument.Load(from);
                var metadata = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
                var node = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "version");
                if (node is not null)
                    node.Value = version;
                document.Save(to);
            }
            else
            {
                from.CopyTo(to);
            }
        }
    }

    static string Combined(ProcessStartSettings settings)
        => settings.FileName + " " + string.Join(' ', settings.Arguments);

    static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met in time.");
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
        return ProcessCapture.Run(start);
    }
}
