using System.Diagnostics;
using ndx;

namespace Tests;

public class ProcessStopTests
{
    [Fact]
    public async Task Unix_sigint_stops_a_console_linger()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var child = StartLinger(ignoreCancel: false);
        await WaitReady(child.Process);
        await child.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task Unix_kill_fallback_after_timeout()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var child = StartLinger(ignoreCancel: true);
        await WaitReady(child.Process);
        await child.StopAsync(TimeSpan.FromMilliseconds(200));
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task Windows_wm_close_stops_a_hidden_window()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var child = StartGuiLinger();
        await WaitReady(child.Process);
        await child.StopAsync(TimeSpan.FromSeconds(5));
        Assert.True(child.HasExited);
    }

    static ChildProcess StartLinger(bool ignoreCancel)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ndx-linger", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "linger.cs");
        File.WriteAllText(file, ignoreCancel
            ? """
              Console.CancelKeyPress += (_, e) => e.Cancel = true;
              Console.WriteLine("ready");
              Thread.Sleep(30_000);
              """
            : """
              var done = new ManualResetEventSlim(false);
              Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
              Console.WriteLine("ready");
              done.Wait();
              """);

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(file);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start linger.");
        return new ChildProcess(process);
    }

    static ChildProcess StartGuiLinger()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ndx-linger-gui", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "linger-gui.cs");
        File.WriteAllText(file,
            """
            using System.Runtime.InteropServices;
            GuiLinger.Run();

            static class GuiLinger
            {
                public static void Run()
                {
                    var wndClass = new WndClass
                    {
                        lpfnWndProc = WndProc,
                        lpszClassName = "ndxLingerGui",
                    };
                    RegisterClass(ref wndClass);
                    var hwnd = CreateWindowEx(0, "ndxLingerGui", "ndx-linger", 0x00CF0000, 0, 0, 80, 80, 0, 0, 0, 0);
                    ShowWindow(hwnd, 0);
                    Console.WriteLine("ready");
                    while (GetMessage(out var msg, 0, 0, 0) > 0)
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                }

                static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
                {
                    if (msg == 0x0010)
                    {
                        PostQuitMessage(0);
                        return 0;
                    }
                    return DefWindowProc(hWnd, msg, wParam, lParam);
                }

                struct WndClass
                {
                    public uint style;
                    public WndProcDel lpfnWndProc;
                    public int cbClsExtra;
                    public int cbWndExtra;
                    public nint hInstance;
                    public nint hIcon;
                    public nint hCursor;
                    public nint hbrBackground;
                    public string? lpszMenuName;
                    public string lpszClassName;
                }

                delegate nint WndProcDel(nint hWnd, uint msg, nint wParam, nint lParam);

                [DllImport("user32.dll", CharSet = CharSet.Unicode)]
                static extern ushort RegisterClass(ref WndClass lpWndClass);

                [DllImport("user32.dll", CharSet = CharSet.Unicode)]
                static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

                [DllImport("user32.dll")]
                static extern bool ShowWindow(nint hWnd, int nCmdShow);

                [DllImport("user32.dll")]
                static extern int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

                [DllImport("user32.dll")]
                static extern bool TranslateMessage(ref Msg lpMsg);

                [DllImport("user32.dll")]
                static extern nint DispatchMessage(ref Msg lpMsg);

                [DllImport("user32.dll")]
                static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

                [DllImport("user32.dll")]
                static extern void PostQuitMessage(int nExitCode);

                struct Msg
                {
                    public nint hwnd;
                    public uint message;
                    public nint wParam;
                    public nint lParam;
                    public uint time;
                    public int ptX;
                    public int ptY;
                }
            }
            """);

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(file);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start GUI linger.");
        return new ChildProcess(process);
    }

    static async Task WaitReady(Process process)
    {
        var read = process.StandardOutput.ReadLineAsync();
        var winner = await Task.WhenAny(read, Task.Delay(60_000));
        if (winner != read)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Linger did not print ready.");
        }

        var line = await read;
        if (!string.Equals(line, "ready", StringComparison.Ordinal))
        {
            var err = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Linger did not become ready: '{line}'. {err}");
        }
    }
}
