namespace ndnx;

/// <summary>
/// Live console writer for download progress. PowerShell 7 on Windows pipes
/// native stderr, so <see cref="Console.IsErrorRedirected"/> is true even in a
/// terminal; <c>CONOUT$</c> still reaches the screen.
/// </summary>
public static class ConsoleProgress
{
    public static TextWriter? TryOpen()
    {
        if (OperatingSystem.IsWindows())
            return TryOpenConOut();

        if (!Console.IsOutputRedirected && !Console.IsErrorRedirected)
            return Console.Error;

        return null;
    }

    static TextWriter? TryOpenConOut()
    {
        try
        {
            var stream = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(stream, Console.OutputEncoding) { AutoFlush = true };
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
