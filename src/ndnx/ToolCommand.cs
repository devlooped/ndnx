namespace ndnx;

/// <summary>
/// Command declared by a downloaded tool package.
/// </summary>
public sealed record ToolCommand(string Name, string EntryPointPath, string Runner);

/// <summary>
/// Builds the process-start settings used to invoke a packaged tool command.
/// </summary>
public static class ToolLauncher
{
    public static ProcessStartSettings CreateStartSettings(
        ToolCommand command,
        IReadOnlyList<string> forwardedArguments,
        bool allowRollForward,
        string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(forwardedArguments);

        return command.Runner switch
        {
            "dotnet" => CreateDotnet(command.EntryPointPath, forwardedArguments, allowRollForward, workingDirectory),
            "executable" => CreateInherited(command.EntryPointPath, forwardedArguments, workingDirectory),
            _ => throw new InvalidOperationException(
                $"Unsupported tool runner '{command.Runner}' for command '{command.Name}'."),
        };
    }

    static ProcessStartSettings CreateDotnet(
        string entryPoint,
        IReadOnlyList<string> forwardedArguments,
        bool allowRollForward,
        string? workingDirectory)
    {
        var arguments = new List<string>();
        if (allowRollForward)
        {
            arguments.Add("--roll-forward");
            arguments.Add("Major");
        }

        arguments.Add("exec");
        arguments.Add(entryPoint);
        arguments.AddRange(forwardedArguments);

        return CreateInherited(ResolveDotnetMuxer(), arguments, workingDirectory);
    }

    static ProcessStartSettings CreateInherited(string fileName, IReadOnlyList<string> arguments, string? workingDirectory) => new()
    {
        FileName = fileName,
        Arguments = arguments,
        UseShellExecute = false,
        RedirectStandardInput = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
        WorkingDirectory = workingDirectory,
    };

    static string ResolveDotnetMuxer()
    {
        var fileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var root in new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"),
        })
        {
            if (string.IsNullOrEmpty(root))
                continue;

            var candidate = Path.Combine(root, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is not null)
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }
}
