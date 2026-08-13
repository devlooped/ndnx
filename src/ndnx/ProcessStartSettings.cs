using System.Diagnostics;

namespace ndnx;

/// <summary>
/// Process start settings for the downloaded tool. ndnx always starts the child
/// without a shell and with inherited standard in/out/error.
/// </summary>
public sealed record ProcessStartSettings
{
    public required string FileName { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public bool UseShellExecute { get; init; }
    public bool RedirectStandardInput { get; init; }
    public bool RedirectStandardOutput { get; init; }
    public bool RedirectStandardError { get; init; }
    public string? WorkingDirectory { get; init; }

    public ProcessStartInfo ToStartInfo()
    {
        var start = new ProcessStartInfo
        {
            FileName = FileName,
            UseShellExecute = UseShellExecute,
            RedirectStandardInput = RedirectStandardInput,
            RedirectStandardOutput = RedirectStandardOutput,
            RedirectStandardError = RedirectStandardError,
        };

        if (WorkingDirectory is { } cwd)
            start.WorkingDirectory = cwd;

        foreach (var argument in Arguments)
            start.ArgumentList.Add(argument);

        return start;
    }
}

public interface IProcessRunner
{
    int Run(ProcessStartSettings settings);
}

public sealed class ProcessRunner : IProcessRunner
{
    public int Run(ProcessStartSettings settings)
    {
        using var process = new Process { StartInfo = settings.ToStartInfo() };
        process.Start();
        process.WaitForExit();
        return process.ExitCode;
    }
}
