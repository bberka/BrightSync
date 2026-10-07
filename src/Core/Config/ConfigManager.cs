using System.Collections.Concurrent;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Serilog;

namespace BrightSync.Core.Config;

/// <summary>
/// Loads and persists configuration to %APPDATA%\BrightSync\config.json.
/// </summary>
[JsonSerializable(typeof(AppConfig))]
internal partial class AppConfigJsonContext : JsonSerializerContext
{
}

public sealed class ConfigManager
{
    private const string ConfigFileName = "config.json";

    private static readonly ConcurrentDictionary<string, object> SynchronizationRoots = new(
        StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly AppConfigJsonContext SerializerContext = new(JsonOptions);

    private readonly string _configPath;
    private readonly string _backupPath;
    private readonly object _synchronizationRoot;
    private readonly Action<bool> _applyStartWithWindows;
    private bool? _lastAppliedStartWithWindows;
    private bool _protectLiveFileOnSave;

    public AppConfig Config { get; }

    public ConfigManager()
        : this(configPath: null, applyStartWithWindows: null)
    {
    }

    /// <summary>
    /// Creates a configuration manager using the supplied path.
    /// The registration callback exists so isolated callers can persist configuration without
    /// touching the user's Windows startup entry.
    /// </summary>
    public ConfigManager(string? configPath, Action<bool>? applyStartWithWindows = null)
    {
        if (configPath is not null && string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("Configuration path cannot be empty.", nameof(configPath));

        configPath ??= GetDefaultConfigPath();
        _configPath = Path.GetFullPath(configPath);
        _backupPath = _configPath + ".bak";
        _synchronizationRoot = SynchronizationRoots.GetOrAdd(
            _configPath,
            static _ => new object());
        _applyStartWithWindows = applyStartWithWindows ?? ApplyStartWithWindows;

        Config = Load();
        _lastAppliedStartWithWindows = Config.StartWithWindows;
    }

    public MonitorProfile GetOrCreateProfile(string deviceName)
    {
        lock (_synchronizationRoot)
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
    }

    public void Save()
    {
        lock (_synchronizationRoot)
        {
            EnsureLiveFileMayBeReplaced();
            var snapshot = CreateValidatedSnapshot();
            var json = JsonSerializer.Serialize(snapshot, SerializerContext.AppConfig);
            WriteAtomically(json, preserveExistingBackup: _protectLiveFileOnSave);
            _protectLiveFileOnSave = false;

            ApplyStartWithWindowsIfChanged(snapshot.StartWithWindows);
            Log.Information(
                "Configuration saved to {ConfigPath}. MonitorProfiles={ProfileCount}, StartWithWindows={StartWithWindows}",
                _configPath, snapshot.Monitors.Count, snapshot.StartWithWindows);
        }
    }

    private AppConfig Load()
    {
        lock (_synchronizationRoot)
        {
            var liveExists = File.Exists(_configPath);
            AppConfig? liveConfig;
            Exception? liveFailure = null;
            if (liveExists && TryLoadConfig(_configPath, out liveConfig, out liveFailure))
            {
                Log.Information("Configuration loaded from {ConfigPath}", _configPath);
                return liveConfig!;
            }

            if (liveExists)
            {
                _protectLiveFileOnSave = true;
                Log.Warning(
                    liveFailure,
                    "Failed to load configuration from {ConfigPath}; attempting backup recovery from {BackupPath}",
                    _configPath,
                    _backupPath);
            }

            var backupExists = File.Exists(_backupPath);
            AppConfig? backupConfig;
            Exception? backupFailure = null;
            if (backupExists && TryLoadConfig(_backupPath, out backupConfig, out backupFailure))
            {
                Log.Warning(
                    "Recovered configuration from backup {BackupPath}; live configuration {ConfigPath} was preserved",
                    _backupPath,
                    _configPath);
                return backupConfig!;
            }

            if (backupExists)
            {
                Log.Error(
                    backupFailure,
                    "Backup configuration {BackupPath} was also invalid; using defaults and preserving both configuration files",
                    _backupPath);
            }
            else if (liveExists)
            {
                Log.Error(
                    liveFailure,
                    "No valid backup was available for {ConfigPath}; using defaults while preserving the live file",
                    _configPath);
            }
            else
            {
                Log.Information("Configuration file {ConfigPath} was not found; using defaults", _configPath);
            }

            return CreateDefaultConfig();
        }
    }

    private AppConfig CreateValidatedSnapshot()
    {
        try
        {
            // The generated serializer round-trip creates a detached snapshot without introducing
            // reflection-based serialization or requiring every mutable model to implement cloning.
            var serializedConfig = JsonSerializer.Serialize(Config, SerializerContext.AppConfig);
            var snapshot = JsonSerializer.Deserialize(serializedConfig, SerializerContext.AppConfig)
                           ?? throw new InvalidDataException("Configuration serialized to a null snapshot.");

            NormalizeConfig(snapshot);
            ValidateConfig(snapshot);
            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            Log.Error(ex, "Configuration validation failed; existing file {ConfigPath} was not changed", _configPath);
            throw new InvalidDataException("Configuration is invalid and was not saved.", ex);
        }
    }

    private void EnsureLiveFileMayBeReplaced()
    {
        if (!_protectLiveFileOnSave || !File.Exists(_configPath))
            return;

        if (TryLoadConfig(_configPath, out _, out var currentFailure))
        {
            Log.Error(
                "Refusing to overwrite readable configuration {ConfigPath} after an earlier load failure; reload the configuration before saving",
                _configPath);
            throw new InvalidOperationException(
                "The configuration became readable after it failed to load. Reload it before saving.");
        }

        if (IsStorageFailure(currentFailure) && File.Exists(_configPath))
        {
            Log.Error(
                currentFailure,
                "Refusing to overwrite configuration {ConfigPath} because it is still inaccessible after an earlier load failure",
                _configPath);
            throw new IOException(
                "The configuration file is still inaccessible and was not overwritten.",
                currentFailure);
        }

        Log.Warning(
            currentFailure,
            "Configuration {ConfigPath} is still invalid; saving a validated replacement while preserving the existing backup",
            _configPath);
    }

    private static bool TryLoadConfig(string path, out AppConfig? config, out Exception? failure)
    {
        try
        {
            var text = File.ReadAllText(path);
            config = JsonSerializer.Deserialize(text, SerializerContext.AppConfig)
                     ?? throw new InvalidDataException("Configuration JSON contained no object.");
            NormalizeConfig(config);
            ValidateConfig(config);
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            config = null;
            failure = ex;
            return false;
        }
    }

    private static void NormalizeConfig(AppConfig config)
    {
        config.Monitors ??= new Dictionary<string, MonitorProfile>();
        config.AutoBrightness ??= AutoBrightnessSettings.CreateDefault();
        config.AutoBrightness.Curve ??= [];
        config.AutoBrightness.EnsureDefaults();

        foreach (var profile in config.Monitors.Values)
        {
            if (profile is not null)
                profile.CustomActions ??= [];
        }
    }

    private static AppConfig CreateDefaultConfig()
    {
        var config = new AppConfig();
        NormalizeConfig(config);
        ValidateConfig(config);
        return config;
    }

    private static void ValidateConfig(AppConfig config)
    {
        if (config.Monitors is null)
            throw new InvalidDataException("Monitors cannot be null.");

        if (config.AutoBrightness is null)
            throw new InvalidDataException("AutoBrightness cannot be null.");

        ValidateRange(config.MasterBrightness, -1, 100, nameof(config.MasterBrightness));
        ValidateRange(config.EnforcementIntervalSeconds, 5, 300, nameof(config.EnforcementIntervalSeconds));
        ValidateRange(config.IdleTimeoutMinutes, 1, 120, nameof(config.IdleTimeoutMinutes));
        ValidateRange(config.IdleReductionPercent, 10, 100, nameof(config.IdleReductionPercent));
        ValidateRange(config.EnergySaverReductionPercent, 5, 50, nameof(config.EnergySaverReductionPercent));
        ValidateRange(config.EyeProtectionReductionPercent, 5, 80, nameof(config.EyeProtectionReductionPercent));
        ValidateRange(config.EyeProtectionDefaultDurationHours, 1, 24,
            nameof(config.EyeProtectionDefaultDurationHours));
        ValidateRange(config.BrightnessBoostPercent, 5, 100, nameof(config.BrightnessBoostPercent));
        ValidateRange(config.BrightnessBoostDefaultDurationHours, 1, 24,
            nameof(config.BrightnessBoostDefaultDurationHours));
        ValidateRange(config.PeriodicMonitorRefreshIntervalMinutes, 5, 180,
            nameof(config.PeriodicMonitorRefreshIntervalMinutes));

        if (!Enum.IsDefined(config.AutoInstallMode))
            throw new InvalidDataException($"{nameof(config.AutoInstallMode)} contains an unknown value.");

        var expectedMinutes = AutoBrightnessSettings.GetDefaultPointMinutes();
        if (config.AutoBrightness.Curve is null || config.AutoBrightness.Curve.Count != expectedMinutes.Length)
            throw new InvalidDataException("AutoBrightness curve has an invalid number of points.");

        for (var index = 0; index < expectedMinutes.Length; index++)
        {
            var point = config.AutoBrightness.Curve[index]
                        ?? throw new InvalidDataException($"AutoBrightness curve point {index} is null.");
            if (point.MinuteOfDay != expectedMinutes[index])
                throw new InvalidDataException("AutoBrightness curve points are not in the expected order.");

            ValidateRange(point.Brightness, 0, 100, $"AutoBrightness.Curve[{index}].Brightness");
        }

        foreach (var pair in config.Monitors)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new InvalidDataException("Monitor profile keys cannot be empty.");

            var profile = pair.Value
                          ?? throw new InvalidDataException($"Monitor profile '{pair.Key}' is null.");
            ValidateRange(profile.MinBrightness, 0, 100, $"{pair.Key}.MinBrightness");
            ValidateRange(profile.MaxBrightness, 0, 100, $"{pair.Key}.MaxBrightness");
            if (profile.MinBrightness > profile.MaxBrightness)
                throw new InvalidDataException($"Monitor profile '{pair.Key}' has an invalid brightness range.");

            if (!double.IsFinite(profile.Multiplier) || profile.Multiplier is < 0.1 or > 3.0)
                throw new InvalidDataException($"Monitor profile '{pair.Key}' has an invalid multiplier.");

            ValidateNonNegative(profile.Contrast, pair.Key, nameof(profile.Contrast));
            ValidateNonNegative(profile.Volume, pair.Key, nameof(profile.Volume));
            ValidateNonNegative(profile.RedGain, pair.Key, nameof(profile.RedGain));
            ValidateNonNegative(profile.GreenGain, pair.Key, nameof(profile.GreenGain));
            ValidateNonNegative(profile.BlueGain, pair.Key, nameof(profile.BlueGain));
            ValidateNonNegative(profile.ColorPreset, pair.Key, nameof(profile.ColorPreset));
            ValidateNonNegative(profile.InputSource, pair.Key, nameof(profile.InputSource));
            ValidateNonNegative(profile.RefreshRate, pair.Key, nameof(profile.RefreshRate));
            ValidateNonNegative(profile.Sharpness, pair.Key, nameof(profile.Sharpness));
            ValidateNonNegative(profile.Saturation, pair.Key, nameof(profile.Saturation));
            ValidateNonNegative(profile.Gamma, pair.Key, nameof(profile.Gamma));
            ValidateNonNegative(profile.PowerState, pair.Key, nameof(profile.PowerState));

            if (profile.CustomActions is null)
                throw new InvalidDataException($"Monitor profile '{pair.Key}' custom actions cannot be null.");

            foreach (var action in profile.CustomActions)
            {
                if (action is null || string.IsNullOrWhiteSpace(action.Name))
                    throw new InvalidDataException($"Monitor profile '{pair.Key}' has an invalid custom action.");
            }
        }
    }

    private static void ValidateRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new InvalidDataException($"{name} must be between {minimum} and {maximum}.");
    }

    private static void ValidateNonNegative(int? value, string profileName, string propertyName)
    {
        if (value < 0)
            throw new InvalidDataException($"Monitor profile '{profileName}' has a negative {propertyName}.");
    }

    private static bool IsStorageFailure(Exception? exception)
        => exception is IOException or UnauthorizedAccessException or SecurityException;

    private void WriteAtomically(string json, bool preserveExistingBackup)
    {
        var directory = Path.GetDirectoryName(_configPath)
                        ?? throw new InvalidOperationException("Configuration path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_configPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       options: FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_configPath))
            {
                File.Replace(
                    temporaryPath,
                    _configPath,
                    preserveExistingBackup ? null : _backupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _configPath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to clean up temporary configuration file {TemporaryPath}", temporaryPath);
            }
        }
    }

    private void ApplyStartWithWindowsIfChanged(bool enable)
    {
        if (_lastAppliedStartWithWindows == enable)
            return;

        try
        {
            _applyStartWithWindows(enable);
            _lastAppliedStartWithWindows = enable;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply StartWithWindows={StartWithWindows}", enable);
        }
    }

    private static string GetDefaultConfigPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BrightSync",
            ConfigFileName);

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
