namespace BrightSync.Core.Monitors;

/// <summary>
/// Builds the key used to associate a runtime monitor with its persisted profile.
/// DisplayConfig's monitor device path is preferred because it is derived from the
/// physical target rather than the volatile Windows DISPLAYn alias.
/// </summary>
public static class MonitorIdentityResolver
{
    internal const string DisplayConfigPrefix = "edid:";
    internal const string HardwarePrefix = "hardware:";
    internal const string DeviceNamePrefix = "display:";

    public static string Resolve(
        string? monitorDevicePath,
        string? hardwareDeviceId,
        string? deviceName)
    {
        var displayConfigIdentity = NormalizeDisplayConfigPath(monitorDevicePath);
        if (!string.IsNullOrWhiteSpace(displayConfigIdentity))
            return DisplayConfigPrefix + displayConfigIdentity;

        var hardwareIdentity = NormalizeHardwareIdentity(hardwareDeviceId);
        if (!string.IsNullOrWhiteSpace(hardwareIdentity))
            return HardwarePrefix + hardwareIdentity;

        return DeviceNamePrefix + (NormalizeDeviceName(deviceName) is { Length: > 0 } normalizedDeviceName
            ? normalizedDeviceName
            : "UNKNOWN");
    }

    /// <summary>
    /// Normalizes identity components so casing, whitespace, and Windows path
    /// separator variants do not produce different profile keys.
    /// </summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var components = value.Trim()
            .Split(['\\', '/', '#'], StringSplitOptions.RemoveEmptyEntries)
            .Select(component => new string(component.Where(character => !char.IsWhiteSpace(character)).ToArray()))
            .Where(component => component.Length > 0)
            .Select(component => component.ToUpperInvariant())
            .ToArray();

        return string.Join('|', components);
    }

    internal static string NormalizeDisplayConfigPath(string? monitorDevicePath)
        => RemoveLeadingComponents(Normalize(monitorDevicePath), "?", "DISPLAY");

    internal static string NormalizeHardwareIdentity(string? hardwareDeviceId)
        => RemoveLeadingComponents(Normalize(hardwareDeviceId), "?", "MONITOR", "DISPLAY");

    internal static string NormalizeDeviceName(string? deviceName)
        => RemoveLeadingComponents(Normalize(deviceName), "?", ".");

    internal static string NormalizeProfileIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return string.Empty;

        var value = identity.Trim();
        foreach (var prefix in new[] { DisplayConfigPrefix, HardwarePrefix, DeviceNamePrefix })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return prefix + Normalize(value[prefix.Length..]);
        }

        return Normalize(value);
    }

    internal static bool IsStableIdentity(string? identity)
        => !string.IsNullOrWhiteSpace(identity) &&
           (identity.StartsWith(DisplayConfigPrefix, StringComparison.OrdinalIgnoreCase) ||
            identity.StartsWith(HardwarePrefix, StringComparison.OrdinalIgnoreCase));

    internal static bool IsDeviceNameFallback(string? identity)
        => !string.IsNullOrWhiteSpace(identity) &&
           identity.StartsWith(DeviceNamePrefix, StringComparison.OrdinalIgnoreCase);

    internal static string CreateDeviceNameFallback(string? deviceName, int? occurrence = null)
    {
        var normalizedDeviceName = NormalizeDeviceName(deviceName);
        if (string.IsNullOrWhiteSpace(normalizedDeviceName))
            normalizedDeviceName = "UNKNOWN";

        return occurrence.HasValue
            ? $"{DeviceNamePrefix}{normalizedDeviceName}|{occurrence.Value}"
            : DeviceNamePrefix + normalizedDeviceName;
    }

    /// <summary>
    /// Duplicate hardware/instance metadata cannot distinguish two physical displays.
    /// Give each runtime entry a deterministic fallback so they never share one profile.
    /// The ordinal is deliberately a conservative fallback: continuity is not promised
    /// for duplicate identities because no existing metadata can identify them safely.
    /// </summary>
    public static void EnsureUniqueIdentities(IList<DdcMonitor> monitors)
    {
        foreach (var group in monitors
                     .Where(monitor => !string.IsNullOrWhiteSpace(monitor.StableIdentity))
                     .GroupBy(monitor => monitor.StableIdentity, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var occurrence = 0;
            foreach (var monitor in group)
                monitor.StableIdentity = CreateDeviceNameFallback(monitor.DeviceName, occurrence++);
        }
    }

    private static string RemoveLeadingComponents(string normalized, params string[] components)
    {
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        var values = normalized.Split('|').ToList();
        while (values.Count > 0 && components.Contains(values[0], StringComparer.OrdinalIgnoreCase))
            values.RemoveAt(0);

        return string.Join('|', values);
    }
}
