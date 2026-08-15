namespace ndnx;

/// <summary>
/// Command declared by a downloaded tool package.
/// </summary>
public sealed record ToolCommand(string Name, string EntryPointPath, string Runner, PackageVersion Version = default);

/// <summary>
/// Builds the process-start settings used to invoke a packaged tool command.
/// </summary>
public static class ToolLauncher
{
    public static ProcessStartSettings CreateStartSettings(
        ToolCommand command,
        IReadOnlyList<string> forwardedArguments,
        bool allowRollForward,
        string? workingDirectory = null,
        string? muxerPath = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(forwardedArguments);

        return command.Runner switch
        {
            "dotnet" => CreateDotnet(command.EntryPointPath, forwardedArguments, allowRollForward, workingDirectory, muxerPath),
            "executable" => CreateInherited(command.EntryPointPath, forwardedArguments, workingDirectory),
            _ => throw new InvalidOperationException(
                $"Unsupported tool runner '{command.Runner}' for command '{command.Name}'."),
        };
    }

    static ProcessStartSettings CreateDotnet(
        string entryPoint,
        IReadOnlyList<string> forwardedArguments,
        bool allowRollForward,
        string? workingDirectory,
        string? muxerPath)
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

        return CreateInherited(muxerPath ?? DotnetMuxer.Resolve() ?? DotnetMuxer.FileName, arguments, workingDirectory);
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
}
