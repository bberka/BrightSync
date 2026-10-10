using System.Globalization;
using System.Text.Json.Serialization;
using BrightSync.Core;
using BrightSync.Core.Brightness;

namespace BrightSync.Cli;

/// <summary>
/// The deliberately small, public shape returned by the read-only CLI status command.
/// Keep this DTO free of configuration paths, monitor identities, and command-server credentials.
/// </summary>
public sealed class CliStatusSnapshot
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset TimestampUtc { get; init; }

    [JsonPropertyName("masterBrightness")]
    public int MasterBrightness { get; init; }

    [JsonPropertyName("automaticBrightnessEnabled")]
    public bool AutomaticBrightnessEnabled { get; init; }

    [JsonPropertyName("eyeProtectionActive")]
    public bool EyeProtectionActive { get; init; }

    [JsonPropertyName("eyeProtectionExpiresUtc")]
    public DateTimeOffset? EyeProtectionExpiresUtc { get; init; }

    [JsonPropertyName("eyeProtectionRemainingSeconds")]
    public int? EyeProtectionRemainingSeconds { get; init; }

    [JsonPropertyName("brightnessBoostActive")]
    public bool BrightnessBoostActive { get; init; }

    [JsonPropertyName("brightnessBoostExpiresUtc")]
    public DateTimeOffset? BrightnessBoostExpiresUtc { get; init; }

    [JsonPropertyName("brightnessBoostRemainingSeconds")]
    public int? BrightnessBoostRemainingSeconds { get; init; }

    [JsonPropertyName("monitorCount")]
    public int MonitorCount { get; init; }

    [JsonPropertyName("controllableMonitorCount")]
    public int ControllableMonitorCount { get; init; }

    public string ToDisplayString()
        => string.Join(Environment.NewLine,
            "BrightSync status",
            $"Version: {Version}",
            $"Timestamp UTC: {TimestampUtc.ToString("O", CultureInfo.InvariantCulture)}",
            $"Master brightness: {MasterBrightness}%",
            $"Automatic brightness: {(AutomaticBrightnessEnabled ? "enabled" : "disabled")}",
            $"Eye protection: {FormatMode(EyeProtectionActive, EyeProtectionExpiresUtc, EyeProtectionRemainingSeconds)}",
            $"Brightness boost: {FormatMode(BrightnessBoostActive, BrightnessBoostExpiresUtc, BrightnessBoostRemainingSeconds)}",
            $"Monitors: {MonitorCount} total, {ControllableMonitorCount} controllable");

    private static string FormatMode(bool active, DateTimeOffset? expiresUtc, int? remainingSeconds)
    {
        if (!active)
            return "inactive";

        if (!expiresUtc.HasValue || !remainingSeconds.HasValue)
            return "active";

        return $"active (expires {expiresUtc.Value.ToString("O", CultureInfo.InvariantCulture)}, " +
               $"{remainingSeconds.Value} seconds remaining)";
    }
}

internal static class CliStatusSnapshotFactory
{
    public static CliStatusSnapshot Create(
        BrightSyncEngine engine,
        AutoBrightnessService autoBrightnessService,
        EyeProtectionService eyeProtectionService,
        BrightnessBoostService brightnessBoostService)
    {
        var monitors = engine.Ddc.GetMonitors();
        return Create(
            engine.MasterBrightness,
            autoBrightnessService.IsEnabled,
            eyeProtectionService.IsEnabled,
            eyeProtectionService.EndTimeUtc,
            brightnessBoostService.IsEnabled,
            brightnessBoostService.EndTimeUtc,
            monitors.Count,
            monitors.Count(monitor => monitor.SupportsDdcCi),
            DateTimeOffset.UtcNow,
            GetVersion());
    }

    internal static CliStatusSnapshot Create(
        int masterBrightness,
        bool automaticBrightnessEnabled,
        bool eyeProtectionActive,
        DateTime? eyeProtectionEndUtc,
        bool brightnessBoostActive,
        DateTime? brightnessBoostEndUtc,
        int monitorCount,
        int controllableMonitorCount,
        DateTimeOffset timestampUtc,
        string version)
    {
        timestampUtc = timestampUtc.ToUniversalTime();
        var eyeProtection = CreateTimedMode(eyeProtectionActive, eyeProtectionEndUtc, timestampUtc);
        var brightnessBoost = CreateTimedMode(brightnessBoostActive, brightnessBoostEndUtc, timestampUtc);

        return new CliStatusSnapshot
        {
            SchemaVersion = 1,
            Version = version,
            TimestampUtc = timestampUtc,
            MasterBrightness = masterBrightness,
            AutomaticBrightnessEnabled = automaticBrightnessEnabled,
            EyeProtectionActive = eyeProtectionActive,
            EyeProtectionExpiresUtc = eyeProtection.expiresUtc,
            EyeProtectionRemainingSeconds = eyeProtection.remainingSeconds,
            BrightnessBoostActive = brightnessBoostActive,
            BrightnessBoostExpiresUtc = brightnessBoost.expiresUtc,
            BrightnessBoostRemainingSeconds = brightnessBoost.remainingSeconds,
            MonitorCount = monitorCount,
            ControllableMonitorCount = controllableMonitorCount
        };
    }

    private static (DateTimeOffset? expiresUtc, int? remainingSeconds) CreateTimedMode(
        bool active,
        DateTime? endUtc,
        DateTimeOffset timestampUtc)
    {
        if (!active || !endUtc.HasValue)
            return (null, null);

        var expiresUtc = new DateTimeOffset(DateTime.SpecifyKind(endUtc.Value, DateTimeKind.Utc));
        var seconds = (expiresUtc - timestampUtc).TotalSeconds;
        var remainingSeconds = seconds <= 0
            ? 0
            : (int)Math.Min(int.MaxValue, Math.Ceiling(seconds));
        return (expiresUtc, remainingSeconds);
    }

    private static string GetVersion()
    {
        try
        {
            return AppVersionInfo.GetCurrentVersion()?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
