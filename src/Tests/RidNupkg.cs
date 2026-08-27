using System.IO.Compression;
using ndx;

namespace Tests;

/// <summary>
/// Writes a RID-tool nupkg in the SDK layout nativepack reads:
/// <c>tools/any/{rid}/ndx[.exe]</c> plus the extra files a real pack includes.
/// </summary>
static class RidNupkg
{
    public static string Write(string directory, string rid, byte[] payload)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"ndx.{rid}.1.0.0.nupkg");
        if (File.Exists(path))
            File.Delete(path);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var binary = NativePacker.BinaryFileName(rid);
        WriteEntry(zip, $"tools/any/{rid}/{binary}", payload);
        WriteEntry(zip, $"tools/any/{rid}/DotnetToolSettings.xml",
            """<?xml version="1.0" encoding="utf-8"?><DotNetCliTool />"""u8.ToArray());
        WriteEntry(zip, $"tools/any/{rid}/{Path.GetFileNameWithoutExtension(binary)}.pdb", "pdb"u8.ToArray());
        WriteEntry(zip, "readme.md", "# ndx"u8.ToArray());
        return path;
    }

    static void WriteEntry(ZipArchive zip, string name, byte[] payload)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(payload);
    }
}
