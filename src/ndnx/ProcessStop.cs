using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ndnx;

/// <summary>
/// Gracefully stops a child we started: SIGINT / Ctrl+C, then Windows
/// <c>WM_CLOSE</c> if the tool is a GUI, then <see cref="Process.Kill()"/> after a timeout.
/// In-process <c>GenerateConsoleCtrlEvent</c> is enough — the parent survives when
/// <see cref="ShutdownScope"/> cancels the Ctrl+C. No stopr helper.
/// </summary>
public static class ProcessStop
{
    const uint CtrlCEvent = 0;
    const uint WmClose = 0x0010;
    const uint GaRoot = 2;

    /// <summary>
    /// Asks <paramref name="process"/> to exit. Does not wait.
    /// </summary>
    public static void Signal(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.HasExited)
            return;

        if (OperatingSystem.IsWindows())
            SignalWindows(process);
        else
            SignalUnix(process.Id);
    }

    [SupportedOSPlatform("windows")]
    static void SignalWindows(Process process)
    {
#if NDNX_WINDOWS
        if (TryCloseGui(process))
            return;

        if (!GenerateConsoleCtrlEvent(CtrlCEvent, 0) && !process.HasExited)
            process.Kill(entireProcessTree: true);
#else
        process.Kill(entireProcessTree: true);
#endif
    }

    static void SignalUnix(int pid)
    {
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", $"-s INT {pid}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            kill?.WaitForExit();
            if (kill is { ExitCode: 0 })
                return;
        }
        catch (Exception)
        {
        }

        try
        {
            Process.GetProcessById(pid).Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

#if NDNX_WINDOWS
    [SupportedOSPlatform("windows")]
    static bool TryCloseGui(Process process)
    {
        process.Refresh();
        if (process.HasExited)
            return true;

        var posted = false;
        if (process.MainWindowHandle != nint.Zero && process.CloseMainWindow())
        {
            posted = true;
            process.Refresh();
            if (process.HasExited)
                return true;
        }

        var state = new GuiCloseState((uint)process.Id);
        var handle = GCHandle.Alloc(state);
        try
        {
            if (process.MainWindowHandle != nint.Zero)
                state.Windows.Add(process.MainWindowHandle);

            var statePtr = GCHandle.ToIntPtr(handle);
            EnumWindows(EnumWindowsByPid, statePtr);
            EnumWindows(EnumHostedParent, statePtr);

            foreach (var hWnd in state.Windows)
            {
                if (PostMessage(hWnd, WmClose, nint.Zero, nint.Zero))
                    posted = true;
            }
        }
        finally
        {
            handle.Free();
        }

        return posted;
    }

    [SupportedOSPlatform("windows")]
    static bool EnumWindowsByPid(nint hWnd, nint lParam)
    {
        var state = (GuiCloseState)GCHandle.FromIntPtr(lParam).Target!;
        GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == state.TargetPid)
            state.Windows.Add(hWnd);
        return true;
    }

    [SupportedOSPlatform("windows")]
    static bool EnumHostedParent(nint hWnd, nint lParam)
    {
        EnumChildWindows(hWnd, EnumChildForHosted, lParam);
        return true;
    }

    [SupportedOSPlatform("windows")]
    static bool EnumChildForHosted(nint hWnd, nint lParam)
    {
        var state = (GuiCloseState)GCHandle.FromIntPtr(lParam).Target!;
        GetWindowThreadProcessId(hWnd, out var pid);
        if (pid != state.TargetPid)
            return true;

        state.Windows.Add(hWnd);
        var parent = GetAncestor(hWnd, GaRoot);
        if (parent != nint.Zero)
            state.Windows.Add(parent);
        return true;
    }

    sealed class GuiCloseState(uint targetPid)
    {
        public uint TargetPid { get; } = targetPid;
        public HashSet<nint> Windows { get; } = [];
    }

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);
#endif
}

/// <summary>
/// Owns Ctrl+C for the evergreen lifetime so an update-stop is not treated as a user shutdown.
/// </summary>
public sealed class ShutdownScope : IDisposable
{
    readonly CancellationTokenSource cts = new();
    int stoppingChild;
    bool disposed;

    public ShutdownScope() => Console.CancelKeyPress += OnCancel;

    public CancellationToken Token => cts.Token;

    public StoppingChildLease StoppingChild()
    {
        Interlocked.Increment(ref stoppingChild);
        return new StoppingChildLease(this);
    }

    internal void EndStoppingChild() => Interlocked.Decrement(ref stoppingChild);

    public void Cancel() => cts.Cancel();

    void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        if (Volatile.Read(ref stoppingChild) == 0)
            cts.Cancel();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Console.CancelKeyPress -= OnCancel;
        cts.Dispose();
    }

    public readonly struct StoppingChildLease(ShutdownScope scope) : IDisposable
    {
        public void Dispose() => scope.EndStoppingChild();
    }
}
