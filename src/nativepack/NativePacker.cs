using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ndnx;

/// <summary>
/// Turns a Native AOT publish directory into the archive + SHA256 that
/// winget, Scoop, and Homebrew download from a GitHub Release.
/// </summary>
public static class NativePacker
{
    public static bool IsWindowsRid(string rid)
        => rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);

    public static string BinaryFileName(string rid)
        => IsWindowsRid(rid) ? "ndnx.exe" : "ndnx";

    public static string ArchiveFileName(string rid, string version)
        => IsWindowsRid(rid)
            ? $"ndnx-{version}-{rid}.zip"
            : $"ndnx-{version}-{rid}.tar.gz";

    public static NativePackResult Pack(
        string publishDirectory,
        string rid,
        string outputDirectory,
        string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var binaryName = BinaryFileName(rid);
        var binaryPath = Path.Combine(publishDirectory, binaryName);
        if (!File.Exists(binaryPath))
        {
            throw new FileNotFoundException(
                $"Published native binary '{binaryName}' was not found in '{publishDirectory}'.",
                binaryPath);
        }

        Directory.CreateDirectory(outputDirectory);

        var archiveName = ArchiveFileName(rid, version);
        var archivePath = Path.Combine(outputDirectory, archiveName);
        if (File.Exists(archivePath))
            File.Delete(archivePath);

        if (IsWindowsRid(rid))
            PackZip(binaryPath, binaryName, archivePath);
        else
            PackTarGz(binaryPath, binaryName, archivePath);

        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath)))
            .ToLowerInvariant();
        var sha256Path = archivePath + ".sha256";
        File.WriteAllText(sha256Path, $"{sha256}  {archiveName}{Environment.NewLine}");

        return new NativePackResult(archivePath, sha256Path, sha256, binaryName);
    }

    static void PackZip(string binaryPath, string binaryName, string archivePath)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(binaryPath, binaryName, CompressionLevel.Optimal);
    }

    static void PackTarGz(string binaryPath, string binaryName, string archivePath)
    {
        using var file = File.Create(archivePath);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
        using var data = new MemoryStream(File.ReadAllBytes(binaryPath));
        var entry = new PaxTarEntry(TarEntryType.RegularFile, binaryName)
        {
            DataStream = data,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                 | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                 | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
        };
        tar.WriteEntry(entry);
    }
}

public sealed record NativePackResult(
    string ArchivePath,
    string Sha256Path,
    string Sha256,
    string BinaryName);
