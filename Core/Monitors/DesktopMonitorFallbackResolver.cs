using WmiLight;

namespace BrightSync.Core.Monitors;

internal static class DesktopMonitorFallbackResolver
{
    public static MonitorNameResolver.MonitorIdentity ResolveIdentity(string adapterDeviceName)
    {
        var hardwareId = MonitorNameResolver.GetHardwareIdForAdapter(adapterDeviceName);
        if (string.IsNullOrWhiteSpace(hardwareId))
            return MonitorNameResolver.MonitorIdentity.Unknown;

        try
        {
            using var connection = new WmiConnection(@"\\.\root\CIMV2");
            foreach (var monitor in connection.CreateQuery("SELECT Name, MonitorType, PNPDeviceID FROM Win32_DesktopMonitor"))
            {
                var pnpDeviceId = monitor["PNPDeviceID"]?.ToString() ?? string.Empty;
                if (!pnpDeviceId.Contains(hardwareId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var friendlyName = FirstNonEmpty(
                    monitor["Name"]?.ToString(),
                    monitor["MonitorType"]?.ToString());
                if (string.IsNullOrWhiteSpace(friendlyName))
                    break;

                var decodedIdentity = MonitorNameResolver.DecodeHardwareId(hardwareId);
                var cleanedModel = friendlyName.Trim();
                if (!string.IsNullOrWhiteSpace(decodedIdentity.ManufacturerName) &&
                    cleanedModel.StartsWith(decodedIdentity.ManufacturerName, StringComparison.OrdinalIgnoreCase))
                {
                    cleanedModel = cleanedModel[decodedIdentity.ManufacturerName.Length..].TrimStart(' ', '-', '_');
                }

                return MonitorNameResolver.BuildIdentityFromParts(decodedIdentity.ManufacturerName, cleanedModel);
            }
        }
        catch
        {
            // Ignore WMI desktop monitor failures and let other fallbacks handle naming.
        }

        return MonitorNameResolver.MonitorIdentity.Unknown;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
