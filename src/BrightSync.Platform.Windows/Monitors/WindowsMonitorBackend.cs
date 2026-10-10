using System.Runtime.InteropServices;
using BrightSync.Core.Interop;
using BrightSync.Platform;
using Serilog;

namespace BrightSync.Core.Monitors;

/// <summary>
/// Windows monitor backend: HMONITOR enumeration, DDC/CI through dxva2, high-level monitor API,
/// and WMI for the internal panel.
/// </summary>
internal sealed class WindowsMonitorBackend(IInternalBrightness internalBrightness) : IMonitorBackend
{
    private const int RetryDelayMilliseconds = 60;

    public void InvalidateCaches() => MonitorNameResolver.InvalidateCache();

    public DdcMonitorSet Enumerate(bool useLegacyDetection, CancellationToken cancellationToken)
    {
        var hMonitors = new List<IntPtr>();
        var monitors = new List<DdcMonitor>();
        var groups = new List<PhysicalMonitorGroup>();

        try
        {
            NativeMethods.EnumDisplayMonitors(
                IntPtr.Zero, IntPtr.Zero,
                (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
                {
                    hMonitors.Add(hMon);
                    return true;
                },
                IntPtr.Zero);

            foreach (var hMonitor in hMonitors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var deviceName = GetDeviceName(hMonitor, out var resW, out var resH);

                var internalDetection = MonitorDetectionResolver.Resolve(deviceName, deviceName, useLegacyDetection);
                if (internalDetection.IsInternal)
                {
                    var internalHdrInfo = internalDetection.HdrInfo;
                    var currentBrightness = internalBrightness.ReadCurrentBrightness();

                    monitors.Add(new DdcMonitor
                    {
                        DeviceName = deviceName,
                        StableIdentity = internalDetection.StableIdentity,
                        ManufacturerName = internalDetection.ManufacturerName,
                        ModelName = internalDetection.ModelName,
                        FriendlyName = internalDetection.FriendlyName,
                        Description = "Internal Display",
                        ResolutionWidth = resW,
                        ResolutionHeight = resH,
                        RefreshRateHz = useLegacyDetection ? 0 : GetRefreshRate(deviceName),
                        ConnectionType = internalDetection.ConnectionType,
                        IsInternal = true,
                        SupportsDdcCi = true,
                        SupportsBrightnessRead = true,
                        MinNativeBrightness = 0,
                        MaxDdcBrightness = 100,
                        LastCommandedPercent = currentBrightness >= 0 ? currentBrightness : 50,
                        BrightnessBackendType = MonitorBrightnessBackend.InternalPanel,
                        BrightnessBackend = "WMI (Internal)",
                        IsHdrSupported = internalHdrInfo.IsHdrSupported,
                        IsHdrEnabled = internalHdrInfo.IsHdrEnabled,
                        SdrWhiteLevelNits = internalHdrInfo.SdrWhiteLevelNits,
                        IsAppleDisplay = false,
                        IsAppleStudioDisplay = false,
                        DetectionBackend = $"{internalDetection.DetectionBackend} + WMI",
                        DetectionDetails = "Internal display controlled via WMI (WmiSetBrightness).",
                        Handle = IntPtr.Zero,
                        Resource = null
                    });

                    Log.Debug(
                        "Detected internal monitor. Device={DeviceName}, FriendlyName={FriendlyName}, SupportsDdcCi=True, IsInternal=True, BrightnessBackend=WMI",
                        deviceName,
                        internalDetection.FriendlyName);

                    continue;
                }

                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
                {
                    Log.Debug("No physical monitors found for HMONITOR {Handle}", hMonitor);
                    continue;
                }

                var physicals = new NativeMethods.PHYSICAL_MONITOR[count];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals))
                {
                    Log.Warning("Failed to resolve physical monitor handles for device {DeviceName}", deviceName);
                    continue;
                }

                var group = new PhysicalMonitorGroup(physicals, deviceName);
                groups.Add(group);
                var primaryDescription = physicals[0].szPhysicalMonitorDescription?.Trim() ?? deviceName;
                var detection = MonitorDetectionResolver.Resolve(deviceName, primaryDescription, useLegacyDetection);
                var hdrInfo = detection.HdrInfo;
                var isAppleDisplay =
                    string.Equals(detection.ManufacturerName, "Apple", StringComparison.OrdinalIgnoreCase) ||
                    detection.FriendlyName.Contains("Apple", StringComparison.OrdinalIgnoreCase);
                var isAppleStudioDisplay =
                    detection.FriendlyName.Contains("Studio Display", StringComparison.OrdinalIgnoreCase) ||
                    detection.ModelName.Contains("Studio Display", StringComparison.OrdinalIgnoreCase);

                for (var i = 0; i < physicals.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pm = physicals[i];
                    var description = pm.szPhysicalMonitorDescription?.Trim() ?? deviceName;
                    var brightnessSupport = MonitorBrightnessResolver.Probe(pm.hPhysicalMonitor);
                    var details = BuildCombinedDetectionDetails(
                        detection.DetectionDetails,
                        brightnessSupport.DetectionDetails,
                        hdrInfo,
                        isAppleStudioDisplay,
                        brightnessSupport.SupportsBrightnessControl);

                    var m = new DdcMonitor
                    {
                        DeviceName = deviceName,
                        StableIdentity = detection.StableIdentity,
                        ManufacturerName = detection.ManufacturerName,
                        ModelName = detection.ModelName,
                        FriendlyName = detection.FriendlyName,
                        Description = description,
                        ResolutionWidth = resW,
                        ResolutionHeight = resH,
                        RefreshRateHz = useLegacyDetection ? 0 : GetRefreshRate(deviceName),
                        ConnectionType = detection.ConnectionType,
                        IsInternal = detection.IsInternal,
                        SupportsDdcCi = brightnessSupport.SupportsBrightnessControl,
                        SupportsBrightnessRead = brightnessSupport.SupportsBrightnessRead,
                        MinNativeBrightness = (int)brightnessSupport.MinimumNativeBrightness,
                        MaxDdcBrightness = (int)brightnessSupport.MaximumNativeBrightness,
                        LastCommandedPercent = brightnessSupport.CurrentBrightnessPercent,
                        BrightnessBackendType = brightnessSupport.Backend,
                        BrightnessBackend = brightnessSupport.BackendLabel,
                        IsHdrSupported = hdrInfo.IsHdrSupported,
                        IsHdrEnabled = hdrInfo.IsHdrEnabled,
                        SdrWhiteLevelNits = hdrInfo.SdrWhiteLevelNits,
                        IsAppleDisplay = isAppleDisplay,
                        IsAppleStudioDisplay = isAppleStudioDisplay,
                        DetectionBackend = $"{detection.DetectionBackend} + {brightnessSupport.BackendLabel}",
                        DetectionDetails = details,
                        Handle = pm.hPhysicalMonitor,
                        Resource = group
                    };

                    ProbeAdvancedCapabilities(m, cancellationToken);
                    monitors.Add(m);

                    Log.Debug(
                        "Detected monitor. DetectionMode={DetectionMode}, Device={DeviceName}, FriendlyName={FriendlyName}, SupportsDdcCi={SupportsDdcCi}, IsInternal={IsInternal}, DetectionBackend={DetectionBackend}, BrightnessBackend={BrightnessBackend}, HdrEnabled={HdrEnabled}",
                        useLegacyDetection ? "Legacy" : "Modern",
                        deviceName,
                        detection.FriendlyName,
                        brightnessSupport.SupportsBrightnessControl,
                        detection.IsInternal,
                        detection.DetectionBackend,
                        brightnessSupport.BackendLabel,
                        hdrInfo.IsHdrEnabled);
                }
            }

            MonitorIdentityResolver.EnsureUniqueIdentities(monitors);
            return new DdcMonitorSet(monitors, groups);
        }
        catch
        {
            foreach (var group in groups)
                group.Dispose();
            throw;
        }
    }

    public bool SetBrightness(DdcMonitor monitor, int brightnessPercent)
        => monitor.BrightnessBackendType switch
        {
            MonitorBrightnessBackend.HighLevelApi => TrySetHighLevelBrightness(monitor, brightnessPercent),
            MonitorBrightnessBackend.WriteOnlyDdcCi => TrySetVcpBrightness(monitor, brightnessPercent, retryCount: 2),
            MonitorBrightnessBackend.LowLevelDdcCi => TrySetVcpBrightness(monitor, brightnessPercent, retryCount: 2),
            MonitorBrightnessBackend.InternalPanel => internalBrightness.TrySetBrightness(brightnessPercent),
            _ => false
        };

    public bool TryGetBrightness(DdcMonitor monitor, out int brightnessPercent)
    {
        brightnessPercent = 0;
        switch (monitor.BrightnessBackendType)
        {
            case MonitorBrightnessBackend.HighLevelApi:
                return TryGetHighLevelBrightness(monitor, out brightnessPercent);
            case MonitorBrightnessBackend.LowLevelDdcCi:
                return TryGetVcpBrightness(monitor, out brightnessPercent, retryCount: 2);
            case MonitorBrightnessBackend.InternalPanel:
                brightnessPercent = internalBrightness.ReadCurrentBrightness();
                return brightnessPercent >= 0;
            default:
                return false;
        }
    }

    public bool SetVcpFeature(DdcMonitor monitor, byte vcpCode, uint value)
        => TrySetVcpFeatureCore(monitor, vcpCode, value, CancellationToken.None);

    public bool GetVcpFeature(DdcMonitor monitor, byte vcpCode, out uint currentValue, out uint maxValue)
        => TryGetVcpFeatureCore(monitor, vcpCode, out currentValue, out maxValue, CancellationToken.None);

    private static string BuildCombinedDetectionDetails(
        string detectionDetails,
        string brightnessDetails,
        HdrDisplayInfo hdrInfo,
        bool isAppleStudioDisplay,
        bool supportsBrightnessControl)
    {
        var parts = new List<string>
        {
            detectionDetails,
            brightnessDetails
        };

        if (hdrInfo.IsHdrSupported)
        {
            var hdrText = hdrInfo.IsHdrEnabled
                ? "HDR is currently enabled."
                : "HDR is supported but currently disabled.";
            if (hdrInfo.SdrWhiteLevelNits > 0)
                hdrText += $" SDR white level is {hdrInfo.SdrWhiteLevelNits} nits.";
            parts.Add(hdrText);
        }

        if (isAppleStudioDisplay)
        {
            parts.Add(supportsBrightnessControl
                ? "Apple Studio Display was detected and a Windows brightness backend is available on this connection."
                : "Apple Studio Display was detected, but no supported Windows brightness backend was exposed on this connection.");
        }

        return string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static bool TrySetVcpFeatureCore(
        DdcMonitor monitor,
        byte vcpCode,
        uint value,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.SetVCPFeature(monitor.Handle, vcpCode, value))
                return true;
            if (attempt < 1)
                Thread.Sleep(RetryDelayMilliseconds);
        }

        return false;
    }

    private static bool TryGetVcpFeatureCore(
        DdcMonitor monitor,
        byte vcpCode,
        out uint currentValue,
        out uint maxValue,
        CancellationToken cancellationToken)
    {
        currentValue = 0;
        maxValue = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                    monitor.Handle,
                    vcpCode,
                    out _,
                    out var current,
                    out var max))
            {
                currentValue = current;
                maxValue = max;
                return true;
            }

            var err = Marshal.GetLastWin32Error();
            Log.Debug("GetVcpFeature failed. Monitor={Monitor}, VcpCode=0x{Vcp:X2}, Error=0x{Error:X8}, Attempt={Attempt}",
                monitor.FriendlyName, vcpCode, err, attempt + 1);

            if (attempt < 1)
                Thread.Sleep(RetryDelayMilliseconds);
        }

        return false;
    }

    private static string? GetCapabilitiesString(IntPtr hMonitor)
    {
        if (!NativeMethods.GetCapabilitiesStringLength(hMonitor, out var length))
        {
            var err = Marshal.GetLastWin32Error();
            Log.Debug("Failed to get capabilities string length. Error=0x{Error:X8}", err);
            return null;
        }

        if (length == 0) return null;

        var buffer = new byte[length];
        if (!NativeMethods.CapabilitiesRequestAndCapabilitiesReply(hMonitor, buffer, length))
        {
            var err = Marshal.GetLastWin32Error();
            Log.Debug("Failed to get capabilities string. Error=0x{Error:X8}", err);
            return null;
        }

        return System.Text.Encoding.ASCII.GetString(buffer).Trim('\0');
    }

    private static void ProbeAdvancedCapabilities(DdcMonitor monitor, CancellationToken cancellationToken)
    {
        if (monitor.IsInternal)
            return;

        var capStr = GetCapabilitiesString(monitor.Handle);
        VcpCapabilities.ProbeAdvancedControls(
            monitor,
            capStr,
            (byte code, out uint current, out uint max) =>
                TryGetVcpFeatureCore(monitor, code, out current, out max, cancellationToken),
            cancellationToken);
    }

    private static bool TrySetVcpBrightness(DdcMonitor monitor, int brightnessPercent, int retryCount)
    {
        var ddcValue = (uint)Math.Round(brightnessPercent / 100.0 * monitor.MaxDdcBrightness);
        for (var attempt = 0; attempt < retryCount; attempt++)
        {
            if (NativeMethods.SetVCPFeature(monitor.Handle, NativeMethods.VCP_BRIGHTNESS, ddcValue))
                return true;

            if (attempt < retryCount - 1)
                Thread.Sleep(RetryDelayMilliseconds);
        }

        return false;
    }

    private static bool TrySetHighLevelBrightness(DdcMonitor monitor, int brightnessPercent)
    {
        var range = Math.Max(1, monitor.MaxDdcBrightness - monitor.MinNativeBrightness);
        var nativeBrightness = (uint)Math.Round(monitor.MinNativeBrightness + (brightnessPercent / 100.0 * range));
        return NativeMethods.SetMonitorBrightness(monitor.Handle, nativeBrightness);
    }

    private static bool TryGetVcpBrightness(DdcMonitor monitor, out int brightnessPercent, int retryCount)
    {
        brightnessPercent = 0;
        for (var attempt = 0; attempt < retryCount; attempt++)
        {
            if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                    monitor.Handle,
                    NativeMethods.VCP_BRIGHTNESS,
                    out _,
                    out var current,
                    out var max) && max > 0)
            {
                brightnessPercent = (int)Math.Round(current * 100.0 / max);
                return true;
            }

            if (attempt < retryCount - 1)
                Thread.Sleep(RetryDelayMilliseconds);
        }

        return false;
    }

    private static bool TryGetHighLevelBrightness(DdcMonitor monitor, out int brightnessPercent)
    {
        brightnessPercent = 0;
        if (!NativeMethods.GetMonitorBrightness(
                monitor.Handle,
                out var min,
                out var current,
                out var max) || max <= min)
        {
            return false;
        }

        brightnessPercent = (int)Math.Round((current - min) * 100.0 / (max - min));
        return true;
    }

    private static string GetDeviceName(IntPtr hMonitor, out int resW, out int resH)
    {
        resW = 0;
        resH = 0;
        var mi = new NativeMethods.MONITORINFOEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
        };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            return $"Monitor_0x{hMonitor:X}";

        resW = mi.rcMonitor.Right - mi.rcMonitor.Left;
        resH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        return mi.szDevice;
    }

    private static int GetRefreshRate(string deviceName)
    {
        var mode = new NativeMethods.DEVMODE
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>()
        };

        return NativeMethods.EnumDisplaySettings(deviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref mode)
            ? (int)Math.Round(Convert.ToDecimal(mode.dmDisplayFrequency))
            : 0;
    }
}
