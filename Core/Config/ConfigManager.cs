using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace BrightSync.Core.Config;

public sealed class MonitorProfile
{
    /// <summary>Legacy field kept for backward compatibility; monitor names are runtime-detected.</summary>
    public string FriendlyName { get; set; } = string.Empty;
    /// <summary>Whether to sync this monitor at all.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Minimum brightness % to ever set on this monitor (0–100).
    /// Prevents the monitor from going completely dark.
    /// </summary>
    public int MinBrightness { get; set; } = 0;
    /// <summary>
    /// Maximum brightness % to ever set on this monitor (0–100).
    /// Useful for very bright monitors that should stay dimmer.
    /// </summary>
    public int MaxBrightness { get; set; } = 100;
    /// <summary>
    /// Scaling factor applied to the internal brightness before clamping.
    /// 1.0 = match internal; 1.2 = 20% brighter; 0.8 = 20% dimmer.
    /// </summary>
    public double Multiplier { get; set; } = 1.0;
}

public sealed class AppConfig
{
    /// <summary>Per-monitor settings keyed by Windows device name (e.g. \\.\DISPLAY2).</summary>
    public Dictionary<string, MonitorProfile> Monitors { get; set; } = new();
    /// <summary>How often (seconds) the engine re-applies brightness to catch drift.</summary>
    public int EnforcementIntervalSeconds { get; set; } = 10;
    /// <summary>Whether to launch BrightSync when the user logs in.</summary>
    public bool StartWithWindows { get; set; } = false;
}

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

    private AppConfig _config;

    public ConfigManager()
    {
        _config = Load();
    }

    public AppConfig Config => _config;

    public MonitorProfile GetOrCreateProfile(string deviceName)
    {
        if (!_config.Monitors.TryGetValue(deviceName, out var profile))
        {
            profile = new MonitorProfile();
            _config.Monitors[deviceName] = profile;
        }

        // Clear any previously persisted custom/generic monitor name; names are detected at runtime now.
        profile.FriendlyName = string.Empty;
        return profile;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, JsonOptions));
        ApplyStartWithWindows(_config.StartWithWindows);
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
        catch { /* corrupt config — start fresh */ }
        return new AppConfig();
    }

    private static void ApplyStartWithWindows(bool enable)
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "BrightSync";
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key == null) return;

        if (enable)
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (!string.IsNullOrEmpty(exePath))
                key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
