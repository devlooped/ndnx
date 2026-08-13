namespace ndnx;

/// <summary>
/// dnx.cmd-compatible argv split: first operand is PACKAGE[@VERSION], listed
/// flags are consumed by ndnx, everything else (including tokens after --) is
/// forwarded to the child.
/// </summary>
public static class ArgParser
{
    static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--prerelease",
        "--yes",
        "-y",
        "--allow-roll-forward",
        "--disable-parallel",
        "--ignore-failed-sources",
        "--no-http-cache",
        "--interactive",
        "--no-cache",
    };

    static readonly HashSet<string> Valued = new(StringComparer.OrdinalIgnoreCase)
    {
        "--source",
        "--add-source",
        "--configfile",
        "--version",
        "--verbosity",
        "-v",
    };

    public static Invocation Parse(params string[] args) => Parse((IReadOnlyList<string>)args);

    public static Invocation Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? packageId = null;
        string? identityVersion = null;
        string? versionOption = null;
        string? configFile = null;
        string? verbosity = null;
        var prerelease = false;
        var yes = false;
        var allowRollForward = false;
        var disableParallel = false;
        var ignoreFailedSources = false;
        var noHttpCache = false;
        var interactive = false;
        var sources = new List<string>();
        var addSources = new List<string>();
        var forwarded = new List<string>();
        var terminated = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (terminated)
            {
                forwarded.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                terminated = true;
                continue;
            }

            if (arg is "-h" or "--help")
            {
                if (packageId is null)
                    return Invocation.Help();

                forwarded.Add(arg);
                continue;
            }

            var (option, inline) = SplitOption(arg);
            if (option is not null && Valued.Contains(option))
            {
                var value = inline;
                if (value is null)
                {
                    if (i + 1 >= args.Count)
                        return Invocation.Failed($"Missing value for {option}.");

                    value = args[++i];
                }

                if (value.Length == 0)
                    return Invocation.Failed($"Missing value for {option}.");

                switch (option.ToLowerInvariant())
                {
                    case "--source":
                        sources.Add(value);
                        break;
                    case "--add-source":
                        addSources.Add(value);
                        break;
                    case "--configfile":
                        configFile = value;
                        break;
                    case "--version":
                        versionOption = value;
                        break;
                    case "--verbosity":
                    case "-v":
                        verbosity = value;
                        break;
                }

                continue;
            }

            if (option is not null && Flags.Contains(option))
            {
                switch (option.ToLowerInvariant())
                {
                    case "--prerelease":
                        prerelease = true;
                        break;
                    case "--yes":
                    case "-y":
                        yes = true;
                        break;
                    case "--allow-roll-forward":
                        allowRollForward = true;
                        break;
                    case "--disable-parallel":
                        disableParallel = true;
                        break;
                    case "--ignore-failed-sources":
                        ignoreFailedSources = true;
                        break;
                    case "--no-http-cache":
                    case "--no-cache":
                        noHttpCache = true;
                        break;
                    case "--interactive":
                        interactive = true;
                        break;
                }

                continue;
            }

            if (arg.StartsWith('-') && packageId is null)
                return Invocation.Failed($"Unrecognized option '{arg}'.");

            if (packageId is null)
            {
                if (!TryParseIdentity(arg, out packageId, out identityVersion, out var identityError))
                    return Invocation.Failed(identityError!);

                continue;
            }

            forwarded.Add(arg);
        }

        if (packageId is null)
        {
            return Invocation.Failed(
                "Required argument missing: specify PACKAGE_NAME or PACKAGE_NAME@VERSION.");
        }

        if (identityVersion is not null && versionOption is not null)
        {
            return Invocation.Failed(
                "Cannot specify a version in the package identity and also with --version.");
        }

        var version = identityVersion ?? versionOption;
        if (version is not null && prerelease)
        {
            return Invocation.Failed(
                $"The --prerelease option cannot be used with a specific version ({version}).");
        }

        if (version is not null && !PackageVersion.TryParse(version, out _) && !VersionRange.TryParse(version, out _))
            return Invocation.Failed($"Invalid version '{version}'.");

        return new Invocation
        {
            Success = true,
            PackageId = packageId,
            Version = version,
            Prerelease = prerelease,
            Yes = yes,
            AllowRollForward = allowRollForward,
            DisableParallel = disableParallel,
            IgnoreFailedSources = ignoreFailedSources,
            NoHttpCache = noHttpCache,
            Interactive = interactive,
            ConfigFile = configFile,
            Verbosity = verbosity,
            Sources = sources,
            AddSources = addSources,
            ForwardedArguments = forwarded,
        };
    }

    static bool TryParseIdentity(string token, out string? packageId, out string? version, out string? error)
    {
        packageId = null;
        version = null;
        error = null;

        var at = token.IndexOf('@');
        if (at < 0)
        {
            if (token.Length == 0)
            {
                error = "Package identity is empty.";
                return false;
            }

            packageId = token;
            return true;
        }

        packageId = token[..at];
        version = token[(at + 1)..];
        if (packageId.Length == 0)
        {
            error = "Package identity is missing a package id.";
            return false;
        }

        if (version.Length == 0)
        {
            version = null;
        }

        return true;
    }

    static (string? Name, string? InlineValue) SplitOption(string token)
    {
        if (token.Length < 2 || token[0] != '-')
            return (null, null);

        var separator = token.IndexOfAny(['=', ':']);
        if (separator < 0)
            return (token, null);

        return (token[..separator], token[(separator + 1)..]);
    }
}
