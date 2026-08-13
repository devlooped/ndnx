namespace ndnx;

public static class PackProgram
{
    public static int Main(string[] args)
    {
        if (args is not [var publishDirectory, var rid, var outputDirectory, ..])
        {
            Console.Error.WriteLine("Usage: nativepack <publishDirectory> <rid> <outputDirectory> [version]");
            return 1;
        }

        var version = args.Length > 3 ? args[3] : "0.0.0";
        try
        {
            var result = NativePacker.Pack(publishDirectory, rid, outputDirectory, version);
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
