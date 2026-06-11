using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Serilog;

namespace BrightSync;

internal static class Program
{
    private const string SingleInstanceMutexName = "BrightSync-SingleInstance-Mutex-Guid-9b3d-098c86e194a9";

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    public static int Main(string[] args)
    {
        // The mutex guards against a second BrightSync instance starting.
        // It is acquired up front so a duplicate launch can show a single
        // native dialog and exit before any Avalonia/Win32 state is created.
        var singleInstance = new Mutex(initiallyOwned: true,
            name: SingleInstanceMutexName,
            createdNew: out var createdNew);

        try
        {
            if (!createdNew)
            {
                MessageBox(IntPtr.Zero, "BrightSync is already running.", "BrightSync",
                    0x00000040 /* MB_OK | MB_ICONINFORMATION */);
                return 1;
            }

            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                return 0;
            }
            catch (Exception ex)
            {
                // Avalonia surfaces fatal startup errors through the dispatcher; this
                // catch guarantees a non-zero exit code and a Serilog line so the
                // crash shows up in the rolling log file even when no UI is up.
                Log.Fatal(ex, "BrightSync terminated with an unhandled exception");
                return 2;
            }
            finally
            {
                Log.CloseAndFlush();
                singleInstance.ReleaseMutex();
            }
        }
        finally
        {
            singleInstance.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
