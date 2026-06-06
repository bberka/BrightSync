using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Serilog;

namespace BrightSync.Core.Config;

/// <summary>
/// Loads and persists configuration to %APPDATA%\BrightSync\config.json.
[JsonSerializable(typeof(AppConfig))]
internal partial class AppConfigJsonContext : JsonSerializerContext
{
}

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

    private static readonly AppConfigJsonContext SerializerContext = new(JsonOptions);

    public AppConfig Config { get; } = Load();

    public MonitorProfile GetOrCreateProfile(string deviceName)
    {
        if (!Config.Monitors.TryGetValue(deviceName, out var profile))
        {
            profile = new MonitorProfile();
            Config.Monitors[deviceName] = profile;
            Log.Debug("Created new monitor profile for {DeviceName}", deviceName);
        }

        // Clear any previously persisted custom/generic monitor name; names are detected at runtime now.
        profile.FriendlyName = string.Empty;
        return profile;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Config, SerializerContext.AppConfig));
        ApplyStartWithWindows(Config.StartWithWindows);
        Log.Information("Configuration saved to {ConfigPath}. MonitorProfiles={ProfileCount}, StartWithWindows={StartWithWindows}",
            ConfigPath, Config.Monitors.Count, Config.StartWithWindows);
    }

    private static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var text = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize(text, SerializerContext.AppConfig) ?? new AppConfig();
                config.AutoBrightness ??= AutoBrightnessSettings.CreateDefault();
                config.AutoBrightness.EnsureDefaults();
                Log.Information("Configuration loaded from {ConfigPath}", ConfigPath);
                return config;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load configuration from {ConfigPath}; using defaults", ConfigPath);
        }

        Log.Information("Using default configuration");
        var defaultConfig = new AppConfig();
        defaultConfig.AutoBrightness.EnsureDefaults();
        return defaultConfig;
    }

    private static void ApplyStartWithWindows(bool enable)
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
