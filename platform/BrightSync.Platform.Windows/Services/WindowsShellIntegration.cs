using System.Diagnostics;
using System.Runtime.InteropServices;
using BrightSync.Core.Interop;
using BrightSync.Platform;
using Serilog;

namespace BrightSync.Core.Services;

internal sealed class WindowsShellIntegration : IShellIntegration
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    public bool TryGetCursorPosition(out int x, out int y)
    {
        if (NativeMethods.GetCursorPos(out var p))
        {
            x = p.x;
            y = p.y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    public bool TryGetTaskbarBounds(out ScreenRect bounds)
    {
        var appBarData = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>()
        };

        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref appBarData) == UIntPtr.Zero)
        {
            bounds = default;
            return false;
        }

        bounds = new ScreenRect(appBarData.rc.Left, appBarData.rc.Top, appBarData.rc.Right, appBarData.rc.Bottom);
        return true;
    }

    public void OpenUrl(string url) => ShellExecute(url);

    public void OpenDisplaySettings() => ShellExecute("ms-settings:display");

    // Notifications on Windows go through the tray balloon; there is no separate toast channel.
    public void ShowNotification(string title, string message)
    {
    }

    public void ShowMessage(string title, string message)
        => MessageBox(IntPtr.Zero, message, title, 0x00000040 /* MB_OK | MB_ICONINFORMATION */);

    public bool TryAttachParentConsole() => AttachConsole(AttachParentProcess);

    private static void ShellExecute(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open {Target}", target);
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);
}
