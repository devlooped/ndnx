namespace ndx;

public static class PackProgram
{
    public static int Main(string[] args)
    {
        if (args is not [var nupkgPath, var rid, var outputDirectory, ..])
        {
            Console.Error.WriteLine("Usage: nativepack <nupkg> <rid> <outputDirectory> [version]");
            return 1;
        }

        var version = args.Length > 3 ? args[3] : "0.0.0";
        try
        {
            var result = NativePacker.Pack(nupkgPath, rid, outputDirectory, version);
            Console.WriteLine(result.ArchivePath);
            Console.WriteLine(result.Sha256Path);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
