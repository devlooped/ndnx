using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using ndnx;

namespace Tests;

public class NativePackerTests
{
    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public void Windows_rid_emits_zip_of_ndnx_exe_and_matching_sha256(string rid)
    {
        using var dir = new TempDir();
        var payload = "windows-native-bytes"u8.ToArray();
        File.WriteAllBytes(Path.Combine(dir.Publish, "ndnx.exe"), payload);

        var result = NativePacker.Pack(dir.Publish, rid, dir.Output, "1.2.3");

        Assert.Equal($"ndnx-1.2.3-{rid}.zip", Path.GetFileName(result.ArchivePath));
        Assert.True(File.Exists(result.ArchivePath));
        Assert.True(File.Exists(result.Sha256Path));

        using (var zip = ZipFile.OpenRead(result.ArchivePath))
        {
            var entry = Assert.Single(zip.Entries);
            Assert.Equal("ndnx.exe", entry.FullName);
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            Assert.Equal(payload, memory.ToArray());
        }

        AssertSha256File(result);
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    public void Unix_rid_emits_targz_of_ndnx_and_matching_sha256(string rid)
    {
        using var dir = new TempDir();
        var payload = "unix-native-bytes"u8.ToArray();
        File.WriteAllBytes(Path.Combine(dir.Publish, "ndnx"), payload);

        var result = NativePacker.Pack(dir.Publish, rid, dir.Output, "4.5.6");

        Assert.Equal($"ndnx-4.5.6-{rid}.tar.gz", Path.GetFileName(result.ArchivePath));

        using var file = File.OpenRead(result.ArchivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var entry = tar.GetNextEntry(copyData: true);
        Assert.NotNull(entry);
        Assert.Equal("ndnx", entry.Name);
        using var memory = new MemoryStream();
        entry.DataStream!.CopyTo(memory);
        Assert.Equal(payload, memory.ToArray());
        Assert.Null(tar.GetNextEntry());

        AssertSha256File(result);
    }

    [Fact]
    public void Program_main_packs_through_the_shipped_entry_point()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(Path.Combine(dir.Publish, "ndnx.exe"), "entry-point"u8.ToArray());

        var code = PackProgram.Main([dir.Publish, "win-x64", dir.Output, "9.9.9"]);

        Assert.Equal(0, code);
        var archive = Path.Combine(dir.Output, "ndnx-9.9.9-win-x64.zip");
        var sha = archive + ".sha256";
        Assert.True(File.Exists(archive));
        Assert.True(File.Exists(sha));
        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal("ndnx.exe", Assert.Single(zip.Entries).FullName);
        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).ToLowerInvariant();
        Assert.StartsWith(expected, File.ReadAllText(sha).Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_binary_is_a_packer_error()
    {
        using var dir = new TempDir();
        var ex = Assert.Throws<FileNotFoundException>(
            () => NativePacker.Pack(dir.Publish, "win-x64", dir.Output, "1.0.0"));
        Assert.Contains("ndnx.exe", ex.Message);
        Assert.Equal(1, PackProgram.Main([dir.Publish, "linux-x64", dir.Output, "1.0.0"]));
    }

    static void AssertSha256File(NativePackResult result)
    {
        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.ArchivePath)))
            .ToLowerInvariant();
        Assert.Equal(expected, result.Sha256);
        var written = File.ReadAllText(result.Sha256Path).Trim();
        Assert.StartsWith(expected, written, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.GetFileName(result.ArchivePath), written);
    }

    sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ndnx-pack-tests", Guid.NewGuid().ToString("n"));
        public string Publish => Path.Combine(Root, "publish");
        public string Output => Path.Combine(Root, "out");

        public TempDir()
        {
            Directory.CreateDirectory(Publish);
            Directory.CreateDirectory(Output);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
