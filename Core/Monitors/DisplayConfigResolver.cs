using System.Runtime.InteropServices;
using BrightSync.Core.Interop;

namespace BrightSync.Core.Monitors;

internal static class DisplayConfigResolver
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static DisplayConfigInfo Resolve(string deviceName)
    {
        try
        {
            if (NativeMethods.GetDisplayConfigBufferSizes(
                    NativeMethods.QDC_ONLY_ACTIVE_PATHS,
                    out var pathCount,
                    out var modeCount) != 0)
            {
                return DisplayConfigInfo.Empty;
            }

            var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[modeCount];
            if (NativeMethods.QueryDisplayConfig(
                    NativeMethods.QDC_ONLY_ACTIVE_PATHS,
                    ref pathCount,
                    paths,
                    ref modeCount,
                    modes,
                    IntPtr.Zero) != 0)
            {
                return DisplayConfigInfo.Empty;
            }

            for (var i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var sourceRequest = new NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_TYPE.GetSourceName,
                        size = (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id
                    }
                };

                if (NativeMethods.DisplayConfigGetDeviceInfo(ref sourceRequest) != 0)
                    continue;

                if (!Comparer.Equals(sourceRequest.viewGdiDeviceName, deviceName))
                    continue;

                var targetRequest = new NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_TYPE.GetTargetName,
                        size = (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id
                    }
                };

                if (NativeMethods.DisplayConfigGetDeviceInfo(ref targetRequest) != 0)
                    return BuildInfo(path, null);

                return BuildInfo(path, targetRequest);
            }
        }
        catch
        {
            // Ignore display-config failures and fall back to the simpler monitor info.
        }

        return DisplayConfigInfo.Empty;
    }

    private static DisplayConfigInfo BuildInfo(
        NativeMethods.DISPLAYCONFIG_PATH_INFO path,
        NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME? target)
    {
        var technology = target?.outputTechnology ?? path.targetInfo.outputTechnology;
        return new DisplayConfigInfo(
            MapConnectionType(technology),
            IsInternal(technology),
            target?.monitorFriendlyDeviceName?.Trim() ?? string.Empty);
    }

    private static bool IsInternal(NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY technology)
        => technology is NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Internal
            or NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Lvds
            or NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DisplayPortEmbedded
            or NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.UdiEmbedded;

    private static string MapConnectionType(NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY technology)
        => technology switch
        {
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Hdmi => "HDMI",
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DisplayPortExternal => "DP",
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.DisplayPortEmbedded => "eDP",
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Dvi => "DVI",
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Lvds => "LVDS",
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Internal => "Internal",
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Hd15 => "VGA",
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Svideo => "S-Video",
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.CompositeVideo => "Composite",
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.ComponentVideo => "Component",
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.UdiExternal => "UDI",
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.UdiEmbedded => "UDI",
            NativeMethods.DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.Miracast => "Miracast",
            _ => string.Empty
        };
}

internal readonly record struct DisplayConfigInfo(
    string ConnectionType,
    bool IsInternal,
    string FriendlyTargetName)
{
    public static DisplayConfigInfo Empty => new(string.Empty, false, string.Empty);
}
