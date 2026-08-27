using System.Diagnostics;

namespace ndx;

public interface IChildProcess : IAsyncDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class ChildProcess : IChildProcess
{
    readonly Process process;

    public ChildProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        this.process = process;
        this.process.EnableRaisingEvents = true;
    }

    public Process Process => process;

    public int Id => process.Id;

    public bool HasExited
    {
        get
        {
            process.Refresh();
            return process.HasExited;
        }
    }

    public int ExitCode => process.ExitCode;

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        => WaitCoreAsync(cancellationToken);

    async Task<int> WaitCoreAsync(CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (HasExited)
            return;

        ProcessStop.Signal(process);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}
