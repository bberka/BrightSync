using WmiLight;
using BrightSync.Core.Interop;

namespace BrightSync.Core.Monitors;

internal static class WmiMonitorConnectionResolver
{
    public static DisplayConfigInfo Resolve(string adapterDeviceName)
    {
        var hardwareId = MonitorNameResolver.GetHardwareIdForAdapter(adapterDeviceName);
        if (string.IsNullOrWhiteSpace(hardwareId))
            return DisplayConfigInfo.Empty;

        try
        {
            using var connection = new WmiConnection(@"\\.\root\WMI");
            foreach (var monitor in connection.CreateQuery(
                         "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams"))
            {
                var instanceName = monitor["InstanceName"]?.ToString() ?? string.Empty;
                if (!instanceName.Contains(hardwareId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var technologyValue = Convert.ToUInt32(monitor["VideoOutputTechnology"] ?? uint.MaxValue);
                var technology = Enum.IsDefined(typeof(NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY),
                    technologyValue)
                    ? (NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY)technologyValue
                    : NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Other;

                return new DisplayConfigInfo(
                    DisplayConfigResolver.MapConnectionType(technology),
                    DisplayConfigResolver.IsInternal(technology),
                    string.Empty,
                    HdrDisplayInfo.Empty);
            }
        }
        catch
        {
            // Ignore WMI connection detection failures and let other fallbacks handle it.
        }

        return DisplayConfigInfo.Empty;
    }
}