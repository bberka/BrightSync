using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace BrightSync.Core.Config;

/// <summary>
/// Loads and persists configuration to %APPDATA%\BrightSync\config.json.
/// </summary>
public sealed class ConfigManager
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrightSync");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AppConfig Config { get; } = Load();

    public MonitorProfile GetOrCreateProfile(string deviceName)
    {
        if (!Config.Monitors.TryGetValue(deviceName, out var profile))
        {
            profile = new MonitorProfile();
            Config.Monitors[deviceName] = profile;
        }

        // Clear any previously persisted custom/generic monitor name; names are detected at runtime now.
        profile.FriendlyName = string.Empty;
        return profile;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, JsonOptions));
        ApplyStartWithWindows(Config.StartWithWindows);
    }

    private static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var text = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(text, JsonOptions) ?? new AppConfig();
            }
        }
        catch
        {
            /* corrupt config — start fresh */
        }

        return new AppConfig();
    }

    private static void ApplyStartWithWindows(bool enable)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "BrightSync";
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        if (key == null) return;

        if (enable)
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (!string.IsNullOrEmpty(exePath))
                key.SetValue(valueName, $"\"{exePath}\" --autostart");
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}