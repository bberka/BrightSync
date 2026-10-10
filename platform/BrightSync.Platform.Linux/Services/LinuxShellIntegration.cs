using System.Diagnostics;
using BrightSync.Platform.Linux.DBus;
using Serilog;
using Tmds.DBus.Protocol;

namespace BrightSync.Platform.Linux.Services;

/// <summary>Desktop-shell integration through freedesktop standards (xdg-open, Notifications, X11 where present).</summary>
internal sealed class LinuxShellIntegration : IShellIntegration
{
    public bool TryGetCursorPosition(out int x, out int y)
        => X11Native.TryGetCursorPosition(out x, out y);

    // Linux has no taskbar-position API. The app infers the panel edge from screen bounds vs work area.
    public bool TryGetTaskbarBounds(out ScreenRect bounds)
    {
        bounds = default;
        return false;
    }

    public void OpenUrl(string url) => StartDetached("xdg-open", url);

    // Desktop environments disagree on how to open display settings; the HDR button is hidden on Linux.
    public void OpenDisplaySettings()
    {
    }

    public void ShowNotification(string title, string message)
    {
        var sent = DBusClient.Session.TryCall(
            "org.freedesktop.Notifications",
            "/org/freedesktop/Notifications",
            "org.freedesktop.Notifications",
            "Notify",
            "susssasa{sv}i",
            (ref MessageWriter w) =>
            {
                w.WriteString("BrightSync");
                w.WriteUInt32(0);
                w.WriteString("brightsync");
                w.WriteString(title);
                w.WriteString(message);
                w.WriteArray(Array.Empty<string>());
                w.WriteDictionary(new Dictionary<string, VariantValue>());
                w.WriteInt32(5000);
            });

        if (!sent)
            StartDetached("notify-send", "--app-name=BrightSync", title, message);
    }

    public void ShowMessage(string title, string message)
    {
        // Prefer a modal dialog; fall back to a notification and stderr when no dialog tool exists.
        if (RunAndWait("zenity", "--info", $"--title={title}", $"--text={message}") ||
            RunAndWait("kdialog", "--title", title, "--msgbox", message))
        {
            return;
        }

        ShowNotification(title, message);
        Console.Error.WriteLine($"{title}: {message}");
    }

    public bool TryAttachParentConsole() => true;

    private static void StartDetached(string fileName, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to start {FileName}", fileName);
        }
    }

    private static bool RunAndWait(string fileName, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null)
                return false;

            process.WaitForExit();
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to run {FileName}", fileName);
            return false;
        }
    }
}
