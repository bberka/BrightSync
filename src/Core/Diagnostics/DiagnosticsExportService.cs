using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;

namespace BrightSync.Core.Diagnostics;

/// <summary>
/// Builds a bounded, redacted diagnostics report from already-collected application state.
/// It never reads configuration files or log files and never performs monitor I/O.
/// </summary>
public sealed class DiagnosticsExportService
{
    public const int MaxReportBytes = 64 * 1024;
    public const int MaxMonitors = 24;
    public const int MaxMessages = 20;

    private const int MaxCapabilityCodes = 64;
    private const int MaxCapabilityValues = 32;
    private const int MaxCapabilityScanLength = 4096;
    private const int MaxMessageLength = 400;
    private const int MaxStringLength = 160;

    /// <summary>
    /// Creates a UTF-8 JSON report. Monitor and message inputs are treated as untrusted text.
    /// </summary>
    public string CreateJson(
        AppConfig config,
        IReadOnlyList<DdcMonitor> monitors,
        IReadOnlyList<string>? recentMessages = null,
        DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(monitors);

        var monitorCount = monitors.Count;
        var monitorReports = new DiagnosticsMonitor[Math.Min(monitorCount, MaxMonitors)];
        for (var index = 0; index < monitorReports.Length; index++)
            monitorReports[index] = BuildMonitorReport(monitors[index]);

        var messageCount = recentMessages?.Count ?? 0;
        var messageReports = new string[Math.Min(messageCount, MaxMessages)];
        for (var index = 0; index < messageReports.Length; index++)
            messageReports[index] = DiagnosticsTextSanitizer.Sanitize(recentMessages![index], MaxMessageLength);

        var report = BuildReport(
            config,
            monitorCount,
            monitorReports,
            messageCount,
            messageReports,
            capturedAtUtc ?? DateTimeOffset.UtcNow,
            reportTruncated: monitorCount > MaxMonitors || messageCount > MaxMessages);

        var bytes = Serialize(report);
        if (bytes.Length > MaxReportBytes)
        {
            // The normal field and collection bounds should keep the report below the limit. Keep
            // a fail-safe minimal report if a future field makes the full report larger.
            report = BuildReport(
                config,
                monitorCount,
                [],
                messageCount,
                [],
                capturedAtUtc ?? DateTimeOffset.UtcNow,
                reportTruncated: true);
            bytes = Serialize(report);
        }

        if (bytes.Length > MaxReportBytes)
            throw new InvalidOperationException("The diagnostics report exceeded its safety limit.");

        return Encoding.UTF8.GetString(bytes);
    }

    private static DiagnosticsReport BuildReport(
        AppConfig config,
        int monitorCount,
        DiagnosticsMonitor[] monitors,
        int messageCount,
        string[] messages,
        DateTimeOffset capturedAtUtc,
        bool reportTruncated)
    {
        var autoBrightness = config.AutoBrightness;
        var configuredMonitorCount = config.Monitors?.Count ?? 0;

        return new DiagnosticsReport(
            SchemaVersion: 1,
            GeneratedAtUtc: capturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Application: new DiagnosticsApplication(
                Name: "BrightSync",
                Version: GetApplicationVersion()),
            System: new DiagnosticsSystem(
                IsWindows: OperatingSystem.IsWindows(),
                OsDescription: DiagnosticsTextSanitizer.Sanitize(RuntimeInformation.OSDescription, MaxStringLength),
                OsVersion: DiagnosticsTextSanitizer.Sanitize(Environment.OSVersion.Version.ToString(), MaxStringLength),
                RuntimeDescription: DiagnosticsTextSanitizer.Sanitize(RuntimeInformation.FrameworkDescription, MaxStringLength),
                RuntimeVersion: DiagnosticsTextSanitizer.Sanitize(Environment.Version.ToString(), MaxStringLength),
                ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
                Is64BitOperatingSystem: Environment.Is64BitOperatingSystem),
            Configuration: new DiagnosticsConfiguration(
                MasterBrightness: config.MasterBrightness,
                ConfiguredMonitorCount: configuredMonitorCount,
                AutoBrightnessEnabled: autoBrightness?.Enabled ?? false,
                AutoBrightnessLockWhenManualChanges: autoBrightness?.LockWhenManualBrightnessChanges ?? false,
                EnforcementEnabled: config.EnforcementEnabled,
                EnforcementIntervalSeconds: config.EnforcementIntervalSeconds,
                UseLegacyDdcCiDetection: config.UseLegacyDdcCiDetection,
                DisableMonitorAccessWhileLocked: config.DisableMonitorAccessWhileLocked,
                IdleReductionEnabled: config.IdleReductionEnabled,
                IdleReductionToMinimum: config.IdleReductionToMinimum,
                IdleIgnoreMediaPlayback: config.IdleIgnoreMediaPlayback,
                IdleTimeoutMinutes: config.IdleTimeoutMinutes,
                EnergySaverReductionEnabled: config.EnergySaverReductionEnabled,
                EyeProtectionEnabled: config.EyeProtectionEnabled,
                BrightnessBoostEnabled: config.BrightnessBoostEnabled,
                PeriodicMonitorRefreshEnabled: config.PeriodicMonitorRefreshEnabled,
                PeriodicMonitorRefreshIntervalMinutes: config.PeriodicMonitorRefreshIntervalMinutes),
            MonitorCount: monitorCount,
            MonitorsTruncated: monitorCount > monitors.Length,
            Monitors: monitors,
            Diagnostics: new DiagnosticsMessages(
                MessageCount: messageCount,
                MessagesTruncated: messageCount > messages.Length,
                RecentMessages: messages,
                LogsIncluded: false),
            ReportTruncated: reportTruncated);
    }

    private static DiagnosticsMonitor BuildMonitorReport(DdcMonitor monitor)
    {
        var rawCapabilities = monitor.RawCapabilitiesString ?? string.Empty;
        var vcpCodes = ParseVcpCodes(rawCapabilities, out var vcpCodesTruncated);
        var presetValues = LimitValues(monitor.SupportedPresets, out var presetValuesTruncated);
        var inputValues = LimitValues(monitor.SupportedInputs, out var inputValuesTruncated);

        return new DiagnosticsMonitor(
            DeviceName: DiagnosticsTextSanitizer.Sanitize(monitor.DeviceName, MaxStringLength),
            ManufacturerName: DiagnosticsTextSanitizer.Sanitize(monitor.ManufacturerName, MaxStringLength),
            ModelName: DiagnosticsTextSanitizer.Sanitize(monitor.ModelName, MaxStringLength),
            FriendlyName: DiagnosticsTextSanitizer.Sanitize(monitor.FriendlyName, MaxStringLength),
            Description: DiagnosticsTextSanitizer.Sanitize(monitor.Description, MaxStringLength),
            ResolutionWidth: monitor.ResolutionWidth,
            ResolutionHeight: monitor.ResolutionHeight,
            RefreshRateHz: monitor.RefreshRateHz,
            ConnectionType: DiagnosticsTextSanitizer.Sanitize(monitor.ConnectionType, MaxStringLength),
            IsInternal: monitor.IsInternal,
            SupportsDdcCi: monitor.SupportsDdcCi,
            SupportsBrightnessRead: monitor.SupportsBrightnessRead,
            MinNativeBrightness: monitor.MinNativeBrightness,
            MaxDdcBrightness: monitor.MaxDdcBrightness,
            LastCommandedPercent: monitor.LastCommandedPercent,
            BrightnessBackend: DiagnosticsTextSanitizer.Sanitize(monitor.BrightnessBackend, MaxStringLength),
            IsHdrSupported: monitor.IsHdrSupported,
            IsHdrEnabled: monitor.IsHdrEnabled,
            SdrWhiteLevelNits: monitor.SdrWhiteLevelNits,
            IsAppleDisplay: monitor.IsAppleDisplay,
            IsAppleStudioDisplay: monitor.IsAppleStudioDisplay,
            DetectionBackend: DiagnosticsTextSanitizer.Sanitize(monitor.DetectionBackend, MaxStringLength),
            DetectionDetails: DiagnosticsTextSanitizer.Sanitize(monitor.DetectionDetails, MaxStringLength),
            Capabilities: new DiagnosticsCapabilities(
                Available: !string.IsNullOrWhiteSpace(rawCapabilities),
                RawLength: rawCapabilities.Length,
                VcpCodesTruncated: vcpCodesTruncated,
                VcpCodes: vcpCodes,
                SupportedPresetCount: monitor.SupportedPresets?.Count ?? 0,
                SupportedPresetValuesTruncated: presetValuesTruncated,
                SupportedPresetValues: presetValues,
                SupportedInputCount: monitor.SupportedInputs?.Count ?? 0,
                SupportedInputValuesTruncated: inputValuesTruncated,
                SupportedInputValues: inputValues),
            AdvancedSupport: new DiagnosticsAdvancedSupport(
                SupportsContrast: monitor.SupportsContrast,
                SupportsVolume: monitor.SupportsVolume,
                SupportsColorPreset: monitor.SupportsColorPreset,
                SupportsRgbGains: monitor.SupportsRgbGains,
                SupportsInputSource: monitor.SupportsInputSource,
                SupportsSharpness: monitor.SupportsSharpness,
                SupportsSaturation: monitor.SupportsSaturation,
                SupportsGamma: monitor.SupportsGamma,
                SupportsPowerControl: monitor.SupportsPowerControl));
    }

    private static int[] LimitValues(IReadOnlyList<uint>? values, out bool truncated)
    {
        var count = values?.Count ?? 0;
        var limitedCount = Math.Min(count, MaxCapabilityValues);
        var result = new int[limitedCount];
        for (var index = 0; index < limitedCount; index++)
            result[index] = values![index] <= int.MaxValue ? (int)values[index] : int.MaxValue;

        truncated = count > limitedCount;
        return result;
    }

    private static string[] ParseVcpCodes(string rawCapabilities, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(rawCapabilities))
            return [];

        var scanLength = Math.Min(rawCapabilities.Length, MaxCapabilityScanLength);
        truncated = rawCapabilities.Length > scanLength;
        var vcpIndex = rawCapabilities.IndexOf("vcp", 0, scanLength, StringComparison.OrdinalIgnoreCase);
        if (vcpIndex < 0)
            return [];

        var openParen = rawCapabilities.IndexOf('(', vcpIndex);
        if (openParen < 0 || openParen >= scanLength)
            return [];

        var depth = 1;
        var closeParen = -1;
        for (var index = openParen + 1; index < scanLength; index++)
        {
            if (rawCapabilities[index] == '(')
                depth++;
            else if (rawCapabilities[index] == ')' && --depth == 0)
            {
                closeParen = index;
                break;
            }
        }

        if (closeParen < 0)
        {
            truncated = true;
            return [];
        }

        var codes = new List<string>(Math.Min(MaxCapabilityCodes, 16));
        var seen = new HashSet<byte>();
        var position = openParen + 1;
        while (position < closeParen)
        {
            while (position < closeParen && char.IsWhiteSpace(rawCapabilities[position]))
                position++;

            var tokenStart = position;
            while (position < closeParen && char.IsLetterOrDigit(rawCapabilities[position]))
                position++;

            if (position == tokenStart)
            {
                position++;
                continue;
            }

            if (byte.TryParse(
                    rawCapabilities.AsSpan(tokenStart, position - tokenStart),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var code) &&
                seen.Add(code))
            {
                if (codes.Count == MaxCapabilityCodes)
                {
                    truncated = true;
                    break;
                }

                codes.Add($"0x{code:X2}");
            }
        }

        return codes.ToArray();
    }

    private static string GetApplicationVersion()
    {
        try
        {
            var version = AppVersionInfo.GetCurrentVersion();
            return DiagnosticsTextSanitizer.Sanitize(version?.ToString() ?? "unknown", MaxStringLength);
        }
        catch
        {
            return "unknown";
        }
    }

    private static byte[] Serialize(DiagnosticsReport report)
        => JsonSerializer.SerializeToUtf8Bytes(report, DiagnosticsJsonContext.Default.DiagnosticsReport);
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DiagnosticsReport))]
internal partial class DiagnosticsJsonContext : JsonSerializerContext
{
}

internal sealed record DiagnosticsReport(
    int SchemaVersion,
    string GeneratedAtUtc,
    DiagnosticsApplication Application,
    DiagnosticsSystem System,
    DiagnosticsConfiguration Configuration,
    int MonitorCount,
    bool MonitorsTruncated,
    DiagnosticsMonitor[] Monitors,
    DiagnosticsMessages Diagnostics,
    bool ReportTruncated);

internal sealed record DiagnosticsApplication(string Name, string Version);

internal sealed record DiagnosticsSystem(
    bool IsWindows,
    string OsDescription,
    string OsVersion,
    string RuntimeDescription,
    string RuntimeVersion,
    string ProcessArchitecture,
    bool Is64BitOperatingSystem);

internal sealed record DiagnosticsConfiguration(
    int MasterBrightness,
    int ConfiguredMonitorCount,
    bool AutoBrightnessEnabled,
    bool AutoBrightnessLockWhenManualChanges,
    bool EnforcementEnabled,
    int EnforcementIntervalSeconds,
    bool UseLegacyDdcCiDetection,
    bool DisableMonitorAccessWhileLocked,
    bool IdleReductionEnabled,
    bool IdleReductionToMinimum,
    bool IdleIgnoreMediaPlayback,
    int IdleTimeoutMinutes,
    bool EnergySaverReductionEnabled,
    bool EyeProtectionEnabled,
    bool BrightnessBoostEnabled,
    bool PeriodicMonitorRefreshEnabled,
    int PeriodicMonitorRefreshIntervalMinutes);

internal sealed record DiagnosticsMonitor(
    string DeviceName,
    string ManufacturerName,
    string ModelName,
    string FriendlyName,
    string Description,
    int ResolutionWidth,
    int ResolutionHeight,
    int RefreshRateHz,
    string ConnectionType,
    bool IsInternal,
    bool SupportsDdcCi,
    bool SupportsBrightnessRead,
    int MinNativeBrightness,
    int MaxDdcBrightness,
    int LastCommandedPercent,
    string BrightnessBackend,
    bool IsHdrSupported,
    bool IsHdrEnabled,
    int SdrWhiteLevelNits,
    bool IsAppleDisplay,
    bool IsAppleStudioDisplay,
    string DetectionBackend,
    string DetectionDetails,
    DiagnosticsCapabilities Capabilities,
    DiagnosticsAdvancedSupport AdvancedSupport);

internal sealed record DiagnosticsCapabilities(
    bool Available,
    int RawLength,
    bool VcpCodesTruncated,
    string[] VcpCodes,
    int SupportedPresetCount,
    bool SupportedPresetValuesTruncated,
    int[] SupportedPresetValues,
    int SupportedInputCount,
    bool SupportedInputValuesTruncated,
    int[] SupportedInputValues);

internal sealed record DiagnosticsAdvancedSupport(
    bool SupportsContrast,
    bool SupportsVolume,
    bool SupportsColorPreset,
    bool SupportsRgbGains,
    bool SupportsInputSource,
    bool SupportsSharpness,
    bool SupportsSaturation,
    bool SupportsGamma,
    bool SupportsPowerControl);

internal sealed record DiagnosticsMessages(
    int MessageCount,
    bool MessagesTruncated,
    string[] RecentMessages,
    bool LogsIncluded);

internal static partial class DiagnosticsTextSanitizer
{
    private const int MaxSanitizerInputLength = 4096;

    public static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (value.Length > MaxSanitizerInputLength)
            return "[redacted-oversized-text]";

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsControl(character))
                builder.Append(character);
        }

        var sanitized = UserProfilePathRegex().Replace(builder.ToString(), "[redacted-user-path]");
        sanitized = SensitiveAssignmentRegex().Replace(sanitized, "$1=[REDACTED]");
        sanitized = BearerTokenRegex().Replace(sanitized, "Bearer [REDACTED]");
        sanitized = BareSecretRegex().Replace(sanitized, "$1 [REDACTED]");

        if (sanitized.Length <= maxLength)
            return sanitized.Trim();

        return sanitized[..maxLength].TrimEnd() + "...";
    }

    [GeneratedRegex(
        """(?i)(?:[a-z]:\\users\\|%userprofile%[\\/]|/users/)[^"'\r\n,;]*""",
        RegexOptions.CultureInvariant)]
    private static partial Regex UserProfilePathRegex();

    [GeneratedRegex(
        """(?i)\b([\w-]*(?:token|secret|password|api[_ -]?key)[\w-]*)\b\s*[:=]\s*(?:"[^"]*"|'[^']*'|[^\s,;&]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentRegex();

    [GeneratedRegex(@"(?i)\bbearer\s+[^\s,;&]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)\b(token|secret|password)\s+[^\s,;&]+", RegexOptions.CultureInvariant)]
    private static partial Regex BareSecretRegex();
}
