using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Serilog;

namespace BrightSync;

class Program
{
    private static Mutex? _mutex;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    public static void Main(string[] args)
    {
        const string appGuid = "BrightSync-SingleInstance-Mutex-Guid-9b3d-098c86e194a9";

        _mutex = new Mutex(true, appGuid, out bool createdNew);
        if (!createdNew)
        {
            MessageBox(IntPtr.Zero, "BrightSync is already running.", "BrightSync",
                0x00000040 /* MB_OK | MB_ICONINFORMATION */);
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}