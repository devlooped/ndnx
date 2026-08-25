using System.Diagnostics;

namespace Tests;

static class ProcessCapture
{
    public static (int ExitCode, string Stdout, string Stderr) Run(ProcessStartInfo start, int timeoutMs = 90_000)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start " + start.FileName);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }

            throw new TimeoutException(
                $"{start.FileName} timed out after {timeoutMs}ms.{Environment.NewLine}{Read(stdoutTask)}{Environment.NewLine}{Read(stderrTask)}");
        }

        Task.WaitAll(stdoutTask, stderrTask);
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    static string Read(Task<string> task)
        => task.IsCompletedSuccessfully ? task.Result : "";
}
