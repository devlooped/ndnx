using System.Runtime.InteropServices;
using ndx;

namespace Tests;

public class FrameworkDependentTests
{
    [Theory]
    [InlineData("net10.0", 10, 0)]
    [InlineData("net8.0", 8, 0)]
    [InlineData("netcoreapp3.1", 3, 1)]
    [InlineData("net5.0", 5, 0)]
    [InlineData("NET10.0", 10, 0)]
    public void Parses_tfm_folders(string folder, int major, int minor)
    {
        Assert.True(FrameworkMoniker.TryParseFolder(folder, out var tfm));
        Assert.Equal(new FrameworkMoniker(major, minor), tfm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("any")]
    [InlineData("win-x64")]
    [InlineData("net")]
    [InlineData("netstandard2.0")]
    public void Rejects_non_tfm_folders(string? folder)
        => Assert.False(FrameworkMoniker.TryParseFolder(folder, out _));

    [Fact]
    public void Lists_netcoreapp_from_shared_folder()
    {
        using var root = new TempDir();
        var muxer = WriteMuxer(root.Path, "8.0.19", "10.0.2");

        var versions = DotnetMuxer.ListNetCoreApp(muxer);
        Assert.Equal(["10.0.2", "8.0.19"], versions.Select(v => v.ToString()));
        Assert.Equal(new FrameworkMoniker(10, 0), DotnetMuxer.HighestNetCoreApp(muxer));
    }

    [Fact]
    public void Missing_shared_folder_yields_no_runtimes()
    {
        using var root = new TempDir();
        var muxer = Path.Combine(root.Path, DotnetMuxer.FileName);
        File.WriteAllText(muxer, "");

        Assert.Empty(DotnetMuxer.ListNetCoreApp(muxer));
        Assert.Null(DotnetMuxer.HighestNetCoreApp(muxer));
    }

    [Fact]
    public void Runtimeconfig_is_preferred_over_tools_folder()
    {
        using var root = new TempDir();
        var tools = Path.Combine(root.Path, "tools", "net8.0", "any");
        Directory.CreateDirectory(tools);
        var entry = Path.Combine(tools, "tool.dll");
        File.WriteAllText(entry, "");
        File.WriteAllText(Path.Combine(tools, "tool.runtimeconfig.json"),
            """
            {
              "runtimeOptions": {
                "tfm": "net8.0",
                "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }
              }
            }
            """);
        var settings = Path.Combine(tools, "DotnetToolSettings.xml");
        File.WriteAllText(settings, "");

        Assert.Equal(new FrameworkMoniker(10, 0), FrameworkDependentGuard.ReadRequirement(entry, settings));
    }

    [Fact]
    public void Tools_folder_is_used_when_runtimeconfig_is_missing()
    {
        using var root = new TempDir();
        var settings = Path.Combine(root.Path, "tools", "net8.0", "any", "DotnetToolSettings.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "");
        var entry = Path.Combine(Path.GetDirectoryName(settings)!, "tool.dll");

        Assert.Equal(new FrameworkMoniker(8, 0), FrameworkDependentGuard.ReadRequirement(entry, settings));
    }

    [Fact]
    public void Guard_accepts_newer_muxer()
    {
        using var root = new TempDir();
        var muxer = WriteMuxer(root.Path, "10.0.2");
        var (entry, settings) = WriteFddLayout(root.Path, "net8.0", "8.0.0");

        FrameworkDependentGuard.EnsureCanExecute(entry, settings, muxer);
    }

    [Fact]
    public void Guard_rejects_older_muxer()
    {
        using var root = new TempDir();
        var muxer = WriteMuxer(root.Path, "8.0.19");
        var (entry, settings) = WriteFddLayout(root.Path, "net10.0", "10.0.0");

        var ex = Assert.Throws<InvalidOperationException>(
            () => FrameworkDependentGuard.EnsureCanExecute(entry, settings, muxer));
        Assert.Contains(".NET 10.0", ex.Message);
        Assert.Contains("8.0.19", ex.Message);
        Assert.Contains(muxer, ex.Message);
    }

    [Fact]
    public void Guard_rejects_missing_muxer()
    {
        using var root = new TempDir();
        var (entry, settings) = WriteFddLayout(root.Path, "net10.0", "10.0.0");

        var ex = Assert.Throws<InvalidOperationException>(
            () => FrameworkDependentGuard.EnsureCanExecute(entry, settings, muxerPath: null));
        Assert.Contains(".NET 10.0", ex.Message);
        Assert.Contains(DotnetMuxer.FileName, ex.Message);
    }

    [Fact]
    public void Locator_single_file_is_returned_even_if_newer_than_host()
    {
        using var root = new TempDir();
        var net10 = WriteSettings(root.Path, "net10.0", "any");
        var muxer = WriteMuxer(root.Path, "8.0.0");

        Assert.Equal(net10, ToolSettingsLocator.Choose([net10], "win-x64", muxer));
    }

    [Fact]
    public void Locator_picks_newest_tfm_the_muxer_can_load()
    {
        using var root = new TempDir();
        var net8 = WriteSettings(root.Path, "net8.0", "any");
        var net10 = WriteSettings(root.Path, "net10.0", "any");
        var muxer = WriteMuxer(root.Path, "8.0.19");

        Assert.Equal(net8, ToolSettingsLocator.Choose([net8, net10], "win-x64", muxer));
    }

    [Fact]
    public void Locator_picks_highest_compatible_tfm_on_newer_muxer()
    {
        using var root = new TempDir();
        var net8 = WriteSettings(root.Path, "net8.0", "any");
        var net10 = WriteSettings(root.Path, "net10.0", "any");
        var muxer = WriteMuxer(root.Path, "10.0.2");

        Assert.Equal(net10, ToolSettingsLocator.Choose([net8, net10], "win-x64", muxer));
    }

    [Fact]
    public void Locator_prefers_host_rid_over_any_at_the_same_tfm()
    {
        using var root = new TempDir();
        var host = RuntimeInformation.RuntimeIdentifier;
        var any = WriteSettings(root.Path, "net10.0", "any");
        var rid = WriteSettings(root.Path, "net10.0", host);
        var muxer = WriteMuxer(root.Path, "10.0.0");

        Assert.Equal(rid, ToolSettingsLocator.Choose([any, rid], host, muxer));
    }

    [Fact]
    public void Locator_does_not_let_rid_beat_a_better_tfm()
    {
        using var root = new TempDir();
        var host = RuntimeInformation.RuntimeIdentifier;
        var oldRid = WriteSettings(root.Path, "net8.0", host);
        var newAny = WriteSettings(root.Path, "net10.0", "any");
        var muxer = WriteMuxer(root.Path, "10.0.0");

        Assert.Equal(newAny, ToolSettingsLocator.Choose([oldRid, newAny], host, muxer));
    }

    [Fact]
    public void Locator_errors_when_every_tfm_is_newer_than_the_muxer()
    {
        using var root = new TempDir();
        var net10 = WriteSettings(root.Path, "net10.0", "any");
        var net11 = WriteSettings(root.Path, "net11.0", "any");
        var muxer = WriteMuxer(root.Path, "8.0.19");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ToolSettingsLocator.Choose([net10, net11], "win-x64", muxer));
        Assert.Contains(".NET 10.0", ex.Message);
        Assert.Contains("8.0.19", ex.Message);
    }

    [Fact]
    public async Task App_does_not_launch_when_muxer_is_too_old()
    {
        using var root = new TempDir();
        var muxer = WriteMuxer(root.Path, "8.0.19");
        var (_, settings) = WriteFddLayout(root.Path, "net10.0", "10.0.0");
        File.WriteAllText(settings,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <DotNetCliTool Version="1">
              <Commands>
                <Command Name="hello-tool" EntryPoint="tool.dll" Runner="dotnet" />
              </Commands>
            </DotNetCliTool>
            """);

        var staging = Path.Combine(root.Path, "nupkg");
        Directory.CreateDirectory(staging);
        CopyDirectory(Path.Combine(root.Path, "tools"), Path.Combine(staging, "tools"));
        File.WriteAllText(Path.Combine(staging, "hello-tool.nuspec"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>hello-tool</id>
                <version>1.0.0</version>
                <authors>ndx</authors>
                <description>fdd fixture</description>
              </metadata>
            </package>
            """);

        var feedDir = Path.Combine(root.Path, "feed");
        Directory.CreateDirectory(feedDir);
        ZipFileFromDirectory(staging, Path.Combine(feedDir, "hello-tool.1.0.0.nupkg"));

        var runner = new RecordingProcessRunner();
        var host = new NdxHost
        {
            WorkingDirectory = root.Path,
            StoreDirectory = Path.Combine(root.Path, "app-store"),
            ProcessRunner = runner,
            Out = new StringWriter(),
            Error = new StringWriter(),
            DotnetMuxer = muxer,
        };

        var code = await App.RunAsync(
            ["hello-tool@1.0.0", "--yes", "--source", feedDir],
            host);

        Assert.Equal(1, code);
        Assert.Equal(0, runner.Calls);
        Assert.Contains(".NET 10.0", host.Error.ToString());
        Assert.Contains("8.0.19", host.Error.ToString());
    }

    static string WriteMuxer(string root, params string[] versions)
    {
        var muxer = Path.Combine(root, "dotnet-root", DotnetMuxer.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(muxer)!);
        File.WriteAllText(muxer, "fake");
        foreach (var version in versions)
        {
            Directory.CreateDirectory(Path.Combine(root, "dotnet-root", "shared", DotnetMuxer.NetCoreApp, version));
        }

        return muxer;
    }

    static (string Entry, string Settings) WriteFddLayout(string root, string tfm, string frameworkVersion)
    {
        var tools = Path.Combine(root, "tools", tfm, "any");
        Directory.CreateDirectory(tools);
        var entry = Path.Combine(tools, "tool.dll");
        File.WriteAllText(entry, "");
        File.WriteAllText(Path.Combine(tools, "tool.runtimeconfig.json"),
            $$"""
            {
              "runtimeOptions": {
                "tfm": "{{tfm}}",
                "framework": { "name": "Microsoft.NETCore.App", "version": "{{frameworkVersion}}" }
              }
            }
            """);
        var settings = Path.Combine(tools, "DotnetToolSettings.xml");
        File.WriteAllText(settings, "");
        return (entry, settings);
    }

    static string WriteSettings(string root, string tfm, string rid)
    {
        var path = Path.Combine(root, "pkg", "tools", tfm, rid, "DotnetToolSettings.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, destination), overwrite: true);
    }

    static void ZipFileFromDirectory(string source, string nupkg)
    {
        if (File.Exists(nupkg))
            File.Delete(nupkg);
        System.IO.Compression.ZipFile.CreateFromDirectory(source, nupkg);
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ndx-fx-tests", Guid.NewGuid().ToString("n"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
