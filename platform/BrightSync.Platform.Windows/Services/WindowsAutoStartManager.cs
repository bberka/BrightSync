using BrightSync.Platform;
using Microsoft.Win32;
using Serilog;

namespace BrightSync.Core.Services;

/// <summary>Registers BrightSync under HKCU\...\Run.</summary>
internal sealed class WindowsAutoStartManager : IAutoStartManager
{
    public void Apply(bool enable)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "BrightSync";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key == null)
        {
            Log.Warning("Startup registry key was unavailable; StartWithWindows change could not be applied");
            return;
        }

        if (enable)
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (!string.IsNullOrEmpty(exePath))
            {
                key.SetValue(valueName, $"\"{exePath}\" --autostart");
                Log.Information("Configured app to start with Windows using {ExePath}", exePath);
            }
            else
            {
                Log.Warning("Failed to resolve executable path for StartWithWindows registration");
            }
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
            Log.Information("Removed StartWithWindows registration");
        }
    }
}
