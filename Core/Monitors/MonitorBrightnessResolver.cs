using System.Text;
using System.Text.RegularExpressions;
using BrightSync.Core.Interop;

namespace BrightSync.Core.Monitors;

internal static class MonitorBrightnessResolver
{
    public static MonitorBrightnessSupport Probe(IntPtr monitorHandle)
    {
        if (TryProbeLowLevelBrightness(monitorHandle, out var lowLevel))
            return lowLevel;

        if (TryProbeHighLevelBrightness(monitorHandle, out var highLevel))
            return highLevel;

        if (TryProbeWriteOnlyBrightness(monitorHandle, out var writeOnly))
            return writeOnly;

        return MonitorBrightnessSupport.Unsupported(
            "Brightness control was not exposed through low-level VCP, high-level monitor APIs, or the capabilities string.");
    }

    private static bool TryProbeLowLevelBrightness(IntPtr monitorHandle, out MonitorBrightnessSupport support)
    {
        support = MonitorBrightnessSupport.Unsupported(string.Empty);
        if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                monitorHandle,
                NativeMethods.VCP_BRIGHTNESS,
                out _,
                out var current,
                out var max) || max == 0)
        {
            return false;
        }

        support = new MonitorBrightnessSupport(
            MonitorBrightnessBackend.LowLevelDdcCi,
            true,
            true,
            0,
            max,
            (int)Math.Round(current * 100.0 / max),
            "DDC/CI",
            $"Brightness control is available through low-level DDC/CI VCP code 0x{NativeMethods.VCP_BRIGHTNESS:X2}.");
        return true;
    }

    private static bool TryProbeHighLevelBrightness(IntPtr monitorHandle, out MonitorBrightnessSupport support)
    {
        support = MonitorBrightnessSupport.Unsupported(string.Empty);
        if (!NativeMethods.GetMonitorCapabilities(monitorHandle, out var capabilities, out _) ||
            (capabilities & NativeMethods.MC_CAPS_BRIGHTNESS) == 0)
        {
            return false;
        }

        if (NativeMethods.GetMonitorBrightness(monitorHandle, out var min, out var current, out var max) && max > min)
        {
            var currentPercent = (int)Math.Round((current - min) * 100.0 / (max - min));
            support = new MonitorBrightnessSupport(
                MonitorBrightnessBackend.HighLevelApi,
                true,
                true,
                min,
                max,
                currentPercent,
                "High-level API",
                "Brightness control is available through the Windows high-level monitor configuration API.");
            return true;
        }

        support = new MonitorBrightnessSupport(
            MonitorBrightnessBackend.HighLevelApi,
            true,
            false,
            0,
            100,
            -1,
            "High-level API",
            "Windows reported high-level monitor brightness support, but the current brightness value could not be read.");
        return true;
    }

    private static bool TryProbeWriteOnlyBrightness(IntPtr monitorHandle, out MonitorBrightnessSupport support)
    {
        support = MonitorBrightnessSupport.Unsupported(string.Empty);
        if (!TryReadCapabilitiesString(monitorHandle, out var capabilities))
            return false;

        if (!IndicatesBrightnessSupport(capabilities))
            return false;

        support = new MonitorBrightnessSupport(
            MonitorBrightnessBackend.WriteOnlyDdcCi,
            true,
            false,
            0,
            100,
            -1,
            "DDC/CI write-only",
            "The monitor capabilities string advertised brightness support even though a direct brightness read did not succeed.");
        return true;
    }

    private static bool TryReadCapabilitiesString(IntPtr monitorHandle, out string capabilities)
    {
        capabilities = string.Empty;
        if (!NativeMethods.GetCapabilitiesStringLength(monitorHandle, out var length) || length == 0 || length > 4096)
            return false;

        var buffer = new byte[length];
        if (!NativeMethods.CapabilitiesRequestAndCapabilitiesReply(monitorHandle, buffer, length))
            return false;

        capabilities = Encoding.ASCII.GetString(buffer).TrimEnd('\0', ' ', '\r', '\n');
        return !string.IsNullOrWhiteSpace(capabilities);
    }

    private static bool IndicatesBrightnessSupport(string capabilities)
    {
        var normalized = capabilities.Replace(" ", string.Empty, StringComparison.Ordinal);
        return Regex.IsMatch(normalized, @"vcp\([^)]*(10|02)", RegexOptions.IgnoreCase);
    }
}
