using ndx;

namespace Tests;

sealed class RecordingProcessRunner : IProcessRunner
{
    public ProcessStartSettings? Last { get; private set; }
    public List<ProcessStartSettings> Starts { get; } = [];
    public int Calls { get; private set; }
    public int ExitCode { get; set; }
    public Queue<IChildProcess> Next { get; } = new();

    public int Run(ProcessStartSettings settings)
    {
        Last = settings;
        Calls++;
        Starts.Add(settings);
        return ExitCode;
    }

    public IChildProcess Start(ProcessStartSettings settings)
    {
        Last = settings;
        Calls++;
        Starts.Add(settings);
        return Next.Count > 0 ? Next.Dequeue() : new FakeChildProcess(ExitCode, exited: true);
    }
}

sealed class FakeChildProcess : IChildProcess
{
    readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeChildProcess(int exitCode, bool exited)
    {
        ExitCode = exitCode;
        HasExited = exited;
        if (exited)
            this.exited.TrySetResult(exitCode);
    }

    public int Id { get; set; } = 1;
    public bool HasExited { get; private set; }
    public int ExitCode { get; private set; }
    public bool StopCalled { get; private set; }

    public void Exit(int code)
    {
        ExitCode = code;
        HasExited = true;
        exited.TrySetResult(code);
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled)
            return exited.Task;

        return WaitCanceledAsync(cancellationToken);
    }

    async Task<int> WaitCanceledAsync(CancellationToken cancellationToken)
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg = cancellationToken.Register(() => canceled.TrySetCanceled(cancellationToken));
        var done = await Task.WhenAny(exited.Task, canceled.Task).ConfigureAwait(false);
        if (done == canceled.Task)
            await canceled.Task.ConfigureAwait(false);
        return await exited.Task.ConfigureAwait(false);
    }

    public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        StopCalled = true;
        Exit(ExitCode);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
