namespace ndnx;

/// <summary>
/// Host overrides used by tests. Production uses <see cref="NdnxHost.CreateDefault"/>.
/// </summary>
public sealed class NdnxHost
{
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();
    public string StoreDirectory { get; init; } = DefaultStoreDirectory();
    public IProcessRunner ProcessRunner { get; init; } = new ProcessRunner();
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Error { get; init; } = Console.Error;
    public HttpMessageHandler? HttpHandler { get; init; }
    public string? ExecutablePath { get; init; }
    public string? CurrentVersion { get; init; }
    public string? UpdateRepository { get; init; }
    public string? RuntimeIdentifier { get; init; }

    public static NdnxHost CreateDefault() => new();

    public static string DefaultStoreDirectory()
        => Environment.GetEnvironmentVariable("NDNX_STORE")
           ?? Path.Combine(
               Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
               "ndnx",
               "store");
}

/// <summary>
/// ndnx entry orchestration: parse, download-to-cache, start child, relay exit code.
/// </summary>
public static class App
{
    const string Usage = """
        Usage: ndnx <PACKAGE_NAME[@VERSION]> [options] [--] [tool arguments]
               ndnx --update [VERSION]
               ndnx --version

        Options:
          --source <SOURCE>          Override package sources
          --add-source <SOURCE>      Add a package source
          --configfile <FILE>        NuGet configuration file
          --version <VERSION>        Package version or version range
          --prerelease               Include prerelease versions
          --yes, -y                  Do not prompt
          --allow-roll-forward       Allow major roll-forward for framework-dependent tools
          --verbosity, -v <LEVEL>    quiet|minimal|normal|detailed|diagnostic
          --disable-parallel         Disable parallel restore
          --ignore-failed-sources    Treat source failures as warnings
          --no-http-cache            Do not use an HTTP cache
          --interactive              Allow interactive restore prompts
          --update [VERSION]         Self-update ndnx to the latest or given version
          --version                  Print the ndnx version
        """;

    public static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(string[] args, NdnxHost? host = null, CancellationToken cancellationToken = default)
    {
        host ??= NdnxHost.CreateDefault();
        var invocation = ArgParser.Parse(args);
        if (invocation.ShowHelp)
        {
            host.Out.WriteLine(Usage);
            return 0;
        }

        if (invocation.ShowVersion)
        {
            host.Out.WriteLine(host.CurrentVersion ?? SelfUpdate.ReadCurrentVersion());
            return 0;
        }

        if (!invocation.Success)
        {
            host.Error.WriteLine(invocation.Error);
            return 1;
        }

        try
        {
            using var http = host.HttpHandler is { } handler
                ? new HttpClient(handler, disposeHandler: false)
                : new HttpClient();

            if (invocation.Update)
                return await SelfUpdate.RunAsync(invocation, host, http, cancellationToken).ConfigureAwait(false);

            var log = IsDetailed(invocation.Verbosity) ? host.Out : null;
            var sources = PackageSources.Resolve(invocation, host.WorkingDirectory);
            var feed = new PackageFeed(http, invocation.IgnoreFailedSources, log);
            var store = new ToolPackageStore(feed, host.StoreDirectory, log);
            var command = await store.GetAsync(invocation, sources, cancellationToken).ConfigureAwait(false);
            var settings = ToolLauncher.CreateStartSettings(
                command,
                invocation.ForwardedArguments,
                invocation.AllowRollForward,
                host.WorkingDirectory);

            if (log is not null)
                log.WriteLine($"Starting {settings.FileName} {string.Join(' ', settings.Arguments)}");

            return host.ProcessRunner.Run(settings);
        }
        catch (Exception ex)
        {
            host.Error.WriteLine(ex.Message);
            if (IsDetailed(invocation.Verbosity))
                host.Error.WriteLine(ex);
            return 1;
        }
    }

    static bool IsDetailed(string? verbosity)
        => verbosity?.ToLowerInvariant() is "detailed" or "diagnostic" or "d" or "diag";
}
