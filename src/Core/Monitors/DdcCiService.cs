using System.Runtime.InteropServices;
using BrightSync.Core.Config;
using BrightSync.Core.Interop;
using Serilog;

namespace BrightSync.Core.Monitors;

/// <summary>
/// Enumerates physical monitors and exposes DDC/CI brightness get/set operations.
/// Thread-safe for concurrent brightness writes from the sync engine.
/// </summary>
public sealed class DdcCiService : IDisposable
{
    private const int RetryDelayMilliseconds = 60;

    private readonly ConfigManager _config;
    private readonly object _lock = new();
    private List<DdcMonitor> _monitors = new();
    private List<PhysicalMonitorGroup> _groups = new();
    private bool _disposed;

    public DdcCiService(ConfigManager config)
    {
        _config = config;
        Refresh();
    }

    /// <summary>Re-enumerates all connected monitors. Call after display topology changes.</summary>
    public void Refresh()
    {
        MonitorNameResolver.InvalidateCache();
        lock (_lock)
        {
            var useLegacyDetection = _config.Config.UseLegacyDdcCiDetection;
            DisposeGroups();
            _groups = new List<PhysicalMonitorGroup>();
            _monitors = new List<DdcMonitor>();
            EnumerateMonitors(useLegacyDetection);
            Log.Information(
                "DDC/CI refresh finished. DetectionMode={DetectionMode}, TotalMonitors={TotalMonitors}, ControllableMonitors={ControllableMonitors}",
                useLegacyDetection ? "Legacy" : "Modern",
                _monitors.Count,
                _monitors.Count(m => m.SupportsDdcCi));
        }
    }

    /// <summary>Returns a snapshot of currently known DDC/CI monitors.</summary>
    public IReadOnlyList<DdcMonitor> GetMonitors()
    {
        lock (_lock)
            return _monitors.ToList();
    }

    /// <summary>
    /// Sets brightness on a specific monitor. brightness is a percentage 0–100.
    /// Maps the percentage to the monitor's native DDC range.
    /// </summary>
    public bool SetBrightness(DdcMonitor monitor, int brightnessPercent)
    {
        brightnessPercent = Math.Clamp(brightnessPercent, 0, 100);
        // Acquire the lock so a concurrent Refresh() cannot destroy the handle mid-call.
        lock (_lock)
        {
            if (_disposed) return false;
            var ok = monitor.BrightnessBackendType switch
            {
                MonitorBrightnessBackend.HighLevelApi => TrySetHighLevelBrightness(monitor, brightnessPercent),
                MonitorBrightnessBackend.WriteOnlyDdcCi => TrySetVcpBrightness(monitor, brightnessPercent,
                    retryCount: 2),
                MonitorBrightnessBackend.LowLevelDdcCi =>
                    TrySetVcpBrightness(monitor, brightnessPercent, retryCount: 2),
                MonitorBrightnessBackend.InternalWmi => TrySetInternalWmiBrightness(brightnessPercent),
                _ => false
            };

            if (ok)
            {
                monitor.LastCommandedPercent = brightnessPercent;
                Log.Debug("Set monitor brightness. Monitor={Monitor}, Brightness={Brightness}%, Backend={Backend}",
                    monitor.FriendlyName, brightnessPercent, monitor.BrightnessBackend);
            }
            else
            {
                Log.Warning("Brightness update failed. Monitor={Monitor}, Brightness={Brightness}%, Backend={Backend}",
                    monitor.FriendlyName, brightnessPercent, monitor.BrightnessBackend);
            }

            return ok;
        }
    }

    /// <summary>
    /// Reads current brightness from the monitor via DDC/CI.
    /// This call is slow (~40–150 ms per monitor) — avoid on the UI thread.
    /// </summary>
    public bool TryGetBrightness(DdcMonitor monitor, out int brightnessPercent)
    {
        brightnessPercent = 0;
        lock (_lock)
        {
            if (_disposed) return false;
            var ok = monitor.BrightnessBackendType switch
            {
                MonitorBrightnessBackend.HighLevelApi => TryGetHighLevelBrightness(monitor, out brightnessPercent),
                MonitorBrightnessBackend.LowLevelDdcCi => TryGetVcpBrightness(monitor, out brightnessPercent,
                    retryCount: 2),
                MonitorBrightnessBackend.InternalWmi => TryGetInternalWmiBrightness(out brightnessPercent),
                _ => false
            };

            if (!ok)
            {
                Log.Debug("Unable to read current brightness for monitor {Monitor} using backend {Backend}",
                    monitor.FriendlyName,
                    monitor.BrightnessBackend);
            }

            return ok;
        }
    }

    // --- Private ---

    private void EnumerateMonitors(bool useLegacyDetection)
    {
        var hMonitors = new List<IntPtr>();
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
            var deviceName = GetDeviceName(hMonitor, out var resW, out var resH);

            var internalDetection = MonitorDetectionResolver.Resolve(deviceName, deviceName, useLegacyDetection);
            if (internalDetection.IsInternal)
            {
                var internalHdrInfo = internalDetection.HdrInfo;
                var tempWatcher = new BrightSync.Core.Brightness.InternalBrightnessWatcher();
                var currentBrightness = tempWatcher.ReadCurrentBrightness();

                _monitors.Add(new DdcMonitor
                {
                    DeviceName = deviceName,
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
                    BrightnessBackendType = MonitorBrightnessBackend.InternalWmi,
                    BrightnessBackend = "WMI (Internal)",
                    IsHdrSupported = internalHdrInfo.IsHdrSupported,
                    IsHdrEnabled = internalHdrInfo.IsHdrEnabled,
                    SdrWhiteLevelNits = internalHdrInfo.SdrWhiteLevelNits,
                    IsAppleDisplay = false,
                    IsAppleStudioDisplay = false,
                    DetectionBackend = $"{internalDetection.DetectionBackend} + WMI",
                    DetectionDetails = "Internal display controlled via WMI (WmiSetBrightness).",
                    Handle = IntPtr.Zero,
                    Group = null
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
            _groups.Add(group);
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
                var pm = physicals[i];
                var description = pm.szPhysicalMonitorDescription?.Trim() ?? deviceName;
                var brightnessSupport = MonitorBrightnessResolver.Probe(pm.hPhysicalMonitor);
                var details = BuildCombinedDetectionDetails(
                    detection.DetectionDetails,
                    brightnessSupport.DetectionDetails,
                    hdrInfo,
                    isAppleStudioDisplay,
                    brightnessSupport.SupportsBrightnessControl);

                _monitors.Add(new DdcMonitor
                {
                    DeviceName = deviceName,
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
                    Group = group
                });

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
    }

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

    private static bool TrySetInternalWmiBrightness(int brightnessPercent)
    {
        var watcher = new BrightSync.Core.Brightness.InternalBrightnessWatcher();
        return watcher.TrySetBrightness(brightnessPercent);
    }

    private static bool TryGetInternalWmiBrightness(out int brightnessPercent)
    {
        var watcher = new BrightSync.Core.Brightness.InternalBrightnessWatcher();
        brightnessPercent = watcher.ReadCurrentBrightness();
        return brightnessPercent >= 0;
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

    private void DisposeGroups()
    {
        foreach (var g in _groups)
            g.Dispose();
        _groups.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Debug("Disposing DDC/CI service");
        lock (_lock)
            DisposeGroups();
    }
}