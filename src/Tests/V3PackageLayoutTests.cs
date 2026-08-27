using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ndx;

namespace Tests;

public class V3PackageLayoutTests
{
    [Fact]
    public void Hash_is_base64_sha512_of_the_root_nuspec()
    {
        using var dir = new TempDir();
        var nupkg = Path.Combine(dir.Path, "pkg.nupkg");
        const string nuspec = """
            <?xml version="1.0"?>
            <package><metadata><id>pkg</id><version>1.0.0</version></metadata></package>
            """;
        using (var zip = ZipFile.Open(nupkg, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "pkg.nuspec", nuspec);
            WriteEntry(zip, "tools/net10.0/any/tool.dll", "payload-does-not-affect-hash");
        }

        var expected = Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(nuspec)));
        Assert.Equal(expected, V3PackageLayout.HashNuspec(nupkg));
        Assert.NotEqual(Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(nupkg))), expected);
        Assert.Equal(88, expected.Length);
    }

    [Fact]
    public void Hash_ignores_nested_nuspec_entries()
    {
        using var dir = new TempDir();
        var nupkg = Path.Combine(dir.Path, "pkg.nupkg");
        const string root = "<package><metadata><id>root</id><version>1.0.0</version></metadata></package>";
        using (var zip = ZipFile.Open(nupkg, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "pkg.nuspec", root);
            WriteEntry(zip, "content/other.nuspec", "<package />");
        }

        Assert.Equal(
            Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(root))),
            V3PackageLayout.HashNuspec(nupkg));
    }

    [Fact]
    public void Metadata_writes_unescaped_plus_in_content_hash()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, V3PackageLayout.MetadataFileName);
        const string hash = "abc+def/ghi==";
        V3PackageLayout.WriteMetadata(path, hash, "https://api.nuget.org/v3/index.json");

        var json = File.ReadAllText(path);
        Assert.Contains("\"contentHash\": \"abc+def/ghi==\"", json);
        Assert.DoesNotContain("\\u002B", json);
        Assert.Contains("\"version\": 2", json);
        Assert.Contains("https://api.nuget.org/v3/index.json", json);
    }

    [Fact]
    public void Extract_skips_opc_internals_and_keeps_tools()
    {
        using var dir = new TempDir();
        var nupkg = Path.Combine(dir.Path, "tool.1.0.0.nupkg");
        using (var zip = ZipFile.Open(nupkg, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "[Content_Types].xml", "<Types />");
            WriteEntry(zip, "_rels/.rels", "<Relationships />");
            WriteEntry(zip, "package/services/metadata/core-properties/foo.psmdcp", "skip");
            WriteEntry(zip, "tools/net10.0/any/DotnetToolSettings.xml", "<ok />");
            WriteEntry(zip, "tools/net10.0/any/tool.dll", "dll");
        }

        var dest = Path.Combine(dir.Path, "out");
        Directory.CreateDirectory(dest);
        File.Copy(nupkg, Path.Combine(dest, "tool.1.0.0.nupkg"));
        V3PackageLayout.ExtractContent(nupkg, dest);

        Assert.True(File.Exists(Path.Combine(dest, "tools", "net10.0", "any", "DotnetToolSettings.xml")));
        Assert.False(File.Exists(Path.Combine(dest, "[Content_Types].xml")));
        Assert.False(Directory.Exists(Path.Combine(dest, "_rels")));
        Assert.True(File.Exists(Path.Combine(dest, "tool.1.0.0.nupkg")));
    }

    [Theory]
    [InlineData("_rels/.rels", false)]
    [InlineData("[Content_Types].xml", false)]
    [InlineData("foo.psmdcp", false)]
    [InlineData("payload.nupkg", false)]
    [InlineData(".nupkg.metadata", false)]
    [InlineData("pkg.1.0.0.nupkg.sha512", false)]
    [InlineData("tools/net10.0/any/DotnetToolSettings.xml", true)]
    [InlineData("pkg.nuspec", true)]
    public void ShouldExtract_filters_opc_and_package_files(string name, bool include)
        => Assert.Equal(include, V3PackageLayout.ShouldExtract(name));

    [Fact]
    public void IsInstalled_accepts_hash_and_nupkg_without_metadata()
    {
        using var dir = new TempDir();
        Assert.True(PackageVersion.TryParse("1.0.0", out var version));
        var install = V3PackageLayout.GetInstallPath(dir.Path, "Hello-Tool", version);
        Directory.CreateDirectory(install);
        File.WriteAllText(V3PackageLayout.GetPackageFilePath(dir.Path, "Hello-Tool", version), "nupkg");
        File.WriteAllText(V3PackageLayout.GetHashPath(install, "Hello-Tool", version), "hash");

        Assert.True(V3PackageLayout.IsInstalled(install, "Hello-Tool", version));
        Assert.True(V3PackageLayout.IsInstalled(install));
    }

    [Fact]
    public void Resolve_prefers_NUGET_PACKAGES()
    {
        using var dir = new TempDir();
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", dir.Path);
            Assert.Equal(Path.GetFullPath(dir.Path), GlobalPackagesFolder.Resolve(dir.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
        }
    }

    [Fact]
    public void Resolve_reads_nuget_config_global_packages_folder()
    {
        using var dir = new TempDir();
        var configured = Path.Combine(dir.Path, "gpf");
        File.WriteAllText(Path.Combine(dir.Path, "nuget.config"),
            $"""
            <?xml version="1.0"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{configured}" />
              </config>
            </configuration>
            """);

        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", null);
            Assert.Equal(Path.GetFullPath(configured), GlobalPackagesFolder.Resolve(dir.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
        }
    }

    [Fact]
    public async Task Store_writes_dnx_nuspec_hash_and_reuses_it()
    {
        using var dir = new TempDir();
        var feedDir = Path.Combine(dir.Path, "feed");
        Directory.CreateDirectory(feedDir);
        WriteTinyToolNupkg(Path.Combine(feedDir, "hello-tool.1.0.0.nupkg"));

        using var http = new HttpClient();
        var storeDir = Path.Combine(dir.Path, "gpf");
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), storeDir);
        var invocation = ArgParser.Parse("hello-tool@1.0.0", "--yes", "--source", feedDir);
        await store.GetAsync(invocation, [feedDir]);

        Assert.True(PackageVersion.TryParse("1.0.0", out var version));
        var install = V3PackageLayout.GetInstallPath(storeDir, "hello-tool", version);
        var nupkg = V3PackageLayout.GetPackageFilePath(storeDir, "hello-tool", version);
        var hashPath = V3PackageLayout.GetHashPath(install, "hello-tool", version);
        var metaPath = V3PackageLayout.GetMetadataPath(install);

        Assert.True(File.Exists(nupkg));
        Assert.Equal(V3PackageLayout.HashNuspec(nupkg), File.ReadAllText(hashPath));
        Assert.Contains(File.ReadAllText(hashPath), File.ReadAllText(metaPath));
        Assert.False(File.Exists(Path.Combine(install, "[Content_Types].xml")));

        var hashTime = File.GetLastWriteTimeUtc(hashPath);
        await store.GetAsync(invocation, [feedDir]);
        Assert.Equal(hashTime, File.GetLastWriteTimeUtc(hashPath));
    }

    static void WriteTinyToolNupkg(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(zip, "[Content_Types].xml", "<Types />");
        WriteEntry(zip, "hello-tool.nuspec",
            """
            <?xml version="1.0"?>
            <package>
              <metadata>
                <id>hello-tool</id>
                <version>1.0.0</version>
                <authors>ndx</authors>
                <description>tiny</description>
              </metadata>
            </package>
            """);
        WriteEntry(zip, "tools/net10.0/any/DotnetToolSettings.xml",
            """
            <?xml version="1.0"?>
            <DotNetCliTool Version="1">
              <Commands>
                <Command Name="hello-tool" EntryPoint="tool.dll" Runner="executable" />
              </Commands>
            </DotNetCliTool>
            """);
        WriteEntry(zip, "tools/net10.0/any/tool.dll", "dll");
    }

    static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ndx-v3-tests", Guid.NewGuid().ToString("n"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
