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
            Log.Information("DDC/CI refresh finished. DetectionMode={DetectionMode}, TotalMonitors={TotalMonitors}, ControllableMonitors={ControllableMonitors}",
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
        var ddcValue = (uint)Math.Round(brightnessPercent / 100.0 * monitor.MaxDdcBrightness);
        // Acquire the lock so a concurrent Refresh() cannot destroy the handle mid-call.
        lock (_lock)
        {
            if (_disposed) return false;
            var ok = NativeMethods.SetVCPFeature(monitor.Handle, NativeMethods.VCP_BRIGHTNESS, ddcValue);
            if (ok)
            {
                monitor.LastCommandedPercent = brightnessPercent;
                Log.Debug("Set monitor brightness. Monitor={Monitor}, Brightness={Brightness}%, DdcValue={DdcValue}",
                    monitor.FriendlyName, brightnessPercent, ddcValue);
            }
            else
            {
                Log.Warning("Native DDC/CI brightness update failed. Monitor={Monitor}, Brightness={Brightness}%",
                    monitor.FriendlyName, brightnessPercent);
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
            var ok = NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                monitor.Handle, NativeMethods.VCP_BRIGHTNESS,
                out _, out var current, out var max);
            if (ok && max > 0)
            {
                brightnessPercent = (int)Math.Round(current * 100.0 / max);
                return true;
            }

            if (!ok)
            {
                Log.Debug("Unable to read current DDC/CI brightness for monitor {Monitor}", monitor.FriendlyName);
            }

            return false;
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

            for (var i = 0; i < physicals.Length; i++)
            {
                var pm = physicals[i];
                var description = pm.szPhysicalMonitorDescription?.Trim() ?? deviceName;
                var supportsDdc = NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                    pm.hPhysicalMonitor, NativeMethods.VCP_BRIGHTNESS,
                    out _, out var current, out var maxVal);

                var maxBrightness = (supportsDdc && maxVal > 0) ? (int)maxVal : 100;

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
                    SupportsDdcCi = supportsDdc,
                    MaxDdcBrightness = maxBrightness,
                    LastCommandedPercent = supportsDdc ? (int)Math.Round(current * 100.0 / maxBrightness) : -1,
                    DetectionBackend = detection.DetectionBackend,
                    DetectionDetails = detection.DetectionDetails,
                    Handle = pm.hPhysicalMonitor,
                    Group = group
                });

                Log.Debug("Detected monitor. DetectionMode={DetectionMode}, Device={DeviceName}, FriendlyName={FriendlyName}, SupportsDdcCi={SupportsDdcCi}, IsInternal={IsInternal}, DetectionBackend={DetectionBackend}",
                    useLegacyDetection ? "Legacy" : "Modern",
                    deviceName,
                    detection.FriendlyName,
                    supportsDdc,
                    detection.IsInternal,
                    detection.DetectionBackend);
            }
        }
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
