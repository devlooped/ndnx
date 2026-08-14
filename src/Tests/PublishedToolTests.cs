using System.Diagnostics;
using System.Runtime.InteropServices;
using ndnx;

namespace Tests;

/// <summary>
/// Live nuget.org checks against published sibling multi-RID tools
/// (stop and winget are Native AOT; go is the same RID-package layout).
/// </summary>
public class PublishedToolTests
{
    const string NugetOrg = PackageSources.NugetOrg;
    const string StopId = "stop";
    const string StopVersion = "2.1.0";
    const string WingetId = "winget";
    const string WingetVersion = "0.13.2";
    const string GoId = "go";
    const string GoVersion = "1.1.1";

    static readonly string Store = Path.Combine(Path.GetTempPath(), "ndnx-published-tool-store");

    [Fact]
    public async Task Published_stop_hops_to_rid_package_and_runs_help()
    {
        var command = await GetPublishedAsync(StopId, StopVersion);
        var expectedRid = ExpectedRid(StopId, out var expectedPackage);

        Assert.Equal("dotnet-stop", command.Name);
        Assert.Equal("executable", command.Runner);
        Assert.True(File.Exists(command.EntryPointPath), command.EntryPointPath);
        Assert.Contains(
            Path.DirectorySeparatorChar + expectedPackage + Path.DirectorySeparatorChar,
            command.EntryPointPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.DirectorySeparatorChar + StopId + Path.DirectorySeparatorChar,
            Path.GetFullPath(command.EntryPointPath),
            StringComparison.OrdinalIgnoreCase);

        var result = LaunchNdnx([StopId + "@" + StopVersion, "--yes", "--source", NugetOrg, "--", "--help"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("timeout", result.Stdout + result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(Cached(expectedPackage, StopVersion), expectedPackage);
        Assert.True(Cached(StopId, StopVersion), StopId);
        Assert.Equal(expectedRid, RidPackageResolver.Resolve(
            RuntimeInformation.RuntimeIdentifier,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["win-x64"] = "win-x64",
                ["win-arm64"] = "win-arm64",
                ["linux-x64"] = "linux-x64",
                ["linux-arm64"] = "linux-arm64",
                ["osx-x64"] = "osx-x64",
                ["osx-arm64"] = "osx-arm64",
                ["any"] = "any",
            }));
    }

    [Fact]
    public async Task Published_go_hops_to_portable_rid_without_any_fallback()
    {
        var command = await GetPublishedAsync(GoId, GoVersion);
        var expectedPackage = $"{GoId}.{ExpectedRid(GoId, out _)}";

        Assert.Equal("dotnet-go", command.Name);
        Assert.True(File.Exists(command.EntryPointPath), command.EntryPointPath);
        Assert.Contains(
            Path.DirectorySeparatorChar + expectedPackage + Path.DirectorySeparatorChar,
            command.EntryPointPath,
            StringComparison.OrdinalIgnoreCase);

        var result = LaunchNdnx([GoId + "@" + GoVersion, "--yes", "--source", NugetOrg, "--", "--help"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("file-based", result.Stdout + result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(Cached(expectedPackage, GoVersion), expectedPackage);
    }

    [Fact]
    public async Task Published_winget_on_windows_hops_to_win_rid()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var command = await GetPublishedAsync(WingetId, WingetVersion);
        Assert.Equal("winget", command.Name);
        Assert.Equal("executable", command.Runner);
        Assert.True(File.Exists(command.EntryPointPath), command.EntryPointPath);
        Assert.True(
            command.EntryPointPath.Contains("winget.win-x64", StringComparison.OrdinalIgnoreCase) ||
            command.EntryPointPath.Contains("winget.win-arm64", StringComparison.OrdinalIgnoreCase),
            command.EntryPointPath);
    }

    [Fact]
    public async Task Published_winget_on_non_windows_names_host_and_declared_rids()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), Store);
        var invocation = ArgParser.Parse(WingetId + "@" + WingetVersion, "--yes", "--source", NugetOrg);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync(invocation, [NugetOrg]));
        Assert.Contains(RuntimeInformation.RuntimeIdentifier, error.Message);
        Assert.Contains("win-x64", error.Message);
        Assert.Contains("win-arm64", error.Message);
        Assert.Contains("Declared RIDs", error.Message);
    }

    static string ExpectedRid(string packageId, out string ridPackageId)
    {
        var declared = packageId switch
        {
            StopId => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["win-x64"] = "stop.win-x64",
                ["win-arm64"] = "stop.win-arm64",
                ["linux-x64"] = "stop.linux-x64",
                ["linux-arm64"] = "stop.linux-arm64",
                ["osx-x64"] = "stop.osx-x64",
                ["osx-arm64"] = "stop.osx-arm64",
                ["any"] = "stop.any",
            },
            GoId => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["win-x64"] = "go.win-x64",
                ["win-arm64"] = "go.win-arm64",
                ["linux-x64"] = "go.linux-x64",
                ["linux-arm64"] = "go.linux-arm64",
                ["osx-x64"] = "go.osx-x64",
                ["osx-arm64"] = "go.osx-arm64",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(packageId)),
        };

        var rid = RidPackageResolver.Resolve(RuntimeInformation.RuntimeIdentifier, declared)
            ?? throw new InvalidOperationException(
                $"No published RID package for {packageId} on {RuntimeInformation.RuntimeIdentifier}.");
        ridPackageId = rid;
        var prefix = packageId + ".";
        return rid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? rid[prefix.Length..]
            : rid;
    }

    static async Task<ToolCommand> GetPublishedAsync(string packageId, string version)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), Store);
        var invocation = ArgParser.Parse($"{packageId}@{version}", "--yes", "--source", NugetOrg);
        return await store.GetAsync(invocation, [NugetOrg]);
    }

    static bool Cached(string packageId, string version)
        => ToolPackageStore.IsCached(Path.Combine(Store, packageId.ToLowerInvariant(), version.ToLowerInvariant()));

    static (int ExitCode, string Stdout, string Stderr) LaunchNdnx(string[] args)
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
        start.Environment["NDNX_STORE"] = Store;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start ndnx.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"ndnx timed out.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }

        return (process.ExitCode, stdout, stderr);
    }
}
