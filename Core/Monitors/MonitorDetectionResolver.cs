namespace BrightSync.Core.Monitors;

internal static class MonitorDetectionResolver
{
    public static MonitorDetectionInfo Resolve(
        string deviceName,
        string physicalDescription,
        bool useLegacyDetection)
    {
        var details = new List<string>();
        var backendParts = new List<string>();

        var identity = useLegacyDetection
            ? MonitorNameResolver.MonitorIdentity.Unknown
            : MonitorNameResolver.ResolveIdentity(deviceName);
        var desktopIdentity = MonitorNameResolver.MonitorIdentity.Unknown;

        if (HasFriendlyIdentity(identity))
        {
            backendParts.Add("WmiMonitorID");
            details.Add("Name from WmiMonitorID.");
        }
        else if (!useLegacyDetection)
        {
            desktopIdentity = DesktopMonitorFallbackResolver.ResolveIdentity(deviceName);
            if (HasFriendlyIdentity(desktopIdentity))
            {
                backendParts.Add("Win32_DesktopMonitor");
                details.Add("Name fallback from Win32_DesktopMonitor.");
            }
            else
            {
                details.Add("No high-confidence monitor name from WMI.");
            }
        }
        else
        {
            details.Add("Legacy DDC/CI detection mode is enabled.");
        }

        var selectedIdentity = HasFriendlyIdentity(identity)
            ? identity
            : HasFriendlyIdentity(desktopIdentity)
                ? desktopIdentity
                : MonitorNameResolver.MonitorIdentity.Unknown;

        var displayConfig = useLegacyDetection
            ? DisplayConfigInfo.Empty
            : DisplayConfigResolver.Resolve(deviceName);
        if (HasConnectionInfo(displayConfig))
        {
            backendParts.Add("DisplayConfig");
            details.Add($"Connection from DisplayConfig{FormatConnectionDetails(displayConfig)}.");
        }
        else if (!useLegacyDetection)
        {
            displayConfig = WmiMonitorConnectionResolver.Resolve(deviceName);
            if (HasConnectionInfo(displayConfig))
            {
                backendParts.Add("WmiMonitorConnectionParams");
                details.Add($"Connection fallback from WmiMonitorConnectionParams{FormatConnectionDetails(displayConfig)}.");
            }
            else
            {
                details.Add("No connection metadata was available from DisplayConfig or WMI.");
            }
        }

        var friendlyName = BuildFriendlyName(selectedIdentity, displayConfig, physicalDescription, deviceName);
        if (string.Equals(friendlyName, physicalDescription, StringComparison.OrdinalIgnoreCase))
            details.Add("Friendly name fell back to the monitor firmware description.");
        else if (string.Equals(friendlyName, deviceName, StringComparison.OrdinalIgnoreCase))
            details.Add("Friendly name fell back to the Windows device name.");

        if (backendParts.Count == 0)
            backendParts.Add(useLegacyDetection ? "Legacy" : "Basic");

        return new MonitorDetectionInfo(
            selectedIdentity.ManufacturerName,
            selectedIdentity.ModelName,
            friendlyName,
            displayConfig.ConnectionType,
            displayConfig.IsInternal,
            displayConfig.HdrInfo,
            string.Join(" + ", backendParts),
            string.Join(" ", details));
    }

    private static bool HasFriendlyIdentity(MonitorNameResolver.MonitorIdentity identity)
        => !string.IsNullOrWhiteSpace(identity.ManufacturerName)
            || !string.IsNullOrWhiteSpace(identity.ModelName)
            || !string.IsNullOrWhiteSpace(identity.FriendlyName) &&
               !string.Equals(identity.FriendlyName, "Unknown Monitor", StringComparison.OrdinalIgnoreCase);

    private static bool HasConnectionInfo(DisplayConfigInfo info)
        => !string.IsNullOrWhiteSpace(info.ConnectionType) || info.IsInternal;

    private static string BuildFriendlyName(
        MonitorNameResolver.MonitorIdentity identity,
        DisplayConfigInfo displayConfig,
        string physicalDescription,
        string deviceName)
    {
        if (HasFriendlyIdentity(identity))
            return identity.FriendlyName;

        if (!string.IsNullOrWhiteSpace(displayConfig.FriendlyTargetName))
            return displayConfig.FriendlyTargetName.Trim();

        if (!string.IsNullOrWhiteSpace(physicalDescription))
            return physicalDescription.Trim();

        return deviceName;
    }

    private static string FormatConnectionDetails(DisplayConfigInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.ConnectionType) && !info.IsInternal)
            return string.Empty;

        if (info.IsInternal && string.IsNullOrWhiteSpace(info.ConnectionType))
            return " (internal panel)";

        if (info.IsInternal)
            return $" ({info.ConnectionType}, internal panel)";

        return $" ({info.ConnectionType})";
    }
}
