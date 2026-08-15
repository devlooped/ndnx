using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ndnx;

/// <summary>
/// NuGet v3 global-packages layout: <c>{id}/{version}/{id}.{version}.nupkg</c>,
/// <c>.nupkg.sha512</c> = Base64(SHA512(nupkg)), <c>.nupkg.metadata</c> last.
/// </summary>
public static class V3PackageLayout
{
    public const string MetadataFileName = ".nupkg.metadata";
    public const string HashFileExtension = ".nupkg.sha512";
    public const int MetadataVersion = 2;

    public static string GetInstallPath(string storeDirectory, string packageId, PackageVersion version)
        => Path.Combine(storeDirectory, Normalize(packageId), Normalize(version));

    public static string GetPackageFileName(string packageId, PackageVersion version)
        => $"{Normalize(packageId)}.{Normalize(version)}.nupkg";

    public static string GetPackageFilePath(string storeDirectory, string packageId, PackageVersion version)
        => Path.Combine(GetInstallPath(storeDirectory, packageId, version), GetPackageFileName(packageId, version));

    public static string GetHashPath(string installPath, string packageId, PackageVersion version)
        => Path.Combine(installPath, $"{Normalize(packageId)}.{Normalize(version)}{HashFileExtension}");

    public static string GetMetadataPath(string installPath)
        => Path.Combine(installPath, MetadataFileName);

    public static bool IsInstalled(string installPath, string packageId, PackageVersion version)
    {
        if (File.Exists(GetMetadataPath(installPath)))
            return true;

        return File.Exists(GetHashPath(installPath, packageId, version))
            && File.Exists(Path.Combine(installPath, $"{Normalize(packageId)}.{Normalize(version)}.nupkg"));
    }

    public static bool IsInstalled(string installPath)
        => File.Exists(GetMetadataPath(installPath))
           || (Directory.Exists(installPath) && Directory.GetFiles(installPath, "*" + HashFileExtension).Length > 0);

    public static string HashNupkg(string nupkgPath)
    {
        using var stream = File.OpenRead(nupkgPath);
        return Convert.ToBase64String(SHA512.HashData(stream));
    }

    public static void WriteHash(string hashPath, string base64Sha512)
        => File.WriteAllText(hashPath, base64Sha512);

    public static void WriteMetadata(string metadataPath, string contentHash, string? source)
    {
        using var stream = File.Create(metadataPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        writer.WriteStartObject();
        writer.WriteNumber("version", MetadataVersion);
        writer.WriteString("contentHash", contentHash);
        if (source is not null)
            writer.WriteString("source", source);
        writer.WriteEndObject();
    }

    public static void ExtractContent(string nupkgPath, string destination)
    {
        var destFull = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(nupkgPath);
        foreach (var entry in zip.Entries)
        {
            if (!ShouldExtract(entry.FullName))
                continue;

            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.GetFullPath(Path.Combine(destination, relative));
            if (!dest.StartsWith(destFull, StringComparison.OrdinalIgnoreCase) &&
                !dest.Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to extract '{entry.FullName}' outside the package folder.");
            }

            if (string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(dest);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    public static bool ShouldExtract(string fullName)
    {
        var normalized = fullName.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        if (fileName.Equals(".rels", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalized.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/_rels/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(normalized);
        if (extension.Equals(".psmdcp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (fileName.Equals(MetadataFileName, StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(HashFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    static string Normalize(string value) => value.ToLowerInvariant();

    static string Normalize(PackageVersion version) => version.ToString().ToLowerInvariant();
}
