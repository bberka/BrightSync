using System.Runtime.InteropServices;
using BrightSync.Core.Interop;

namespace BrightSync.Core.Monitors;

/// <summary>
/// Enumerates physical monitors and exposes DDC/CI brightness get/set operations.
/// Thread-safe for concurrent brightness writes from the sync engine.
/// </summary>
public sealed class DdcCiService : IDisposable
{
    private readonly object _lock = new();
    private List<DdcMonitor> _monitors = new();
    private List<PhysicalMonitorGroup> _groups = new();
    private bool _disposed;

    public DdcCiService()
    {
        Refresh();
    }

    /// <summary>Re-enumerates all connected monitors. Call after display topology changes.</summary>
    public void Refresh()
    {
        MonitorNameResolver.InvalidateCache();
        lock (_lock)
        {
            DisposeGroups();
            _groups = new List<PhysicalMonitorGroup>();
            _monitors = new List<DdcMonitor>();
            EnumerateMonitors();
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
                monitor.LastCommandedPercent = brightnessPercent;
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
            return false;
        }
    }

    // --- Private ---

    private void EnumerateMonitors()
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
                continue;

            var physicals = new NativeMethods.PHYSICAL_MONITOR[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals))
                continue;

            var group = new PhysicalMonitorGroup(physicals, deviceName);
            _groups.Add(group);

            // Resolve friendly name once per HMONITOR (one monitor per adapter port)
            var friendlyName = MonitorNameResolver.Resolve(deviceName);

            for (var i = 0; i < physicals.Length; i++)
            {
                var pm = physicals[i];
                var supportsDdc = NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                    pm.hPhysicalMonitor, NativeMethods.VCP_BRIGHTNESS,
                    out _, out var current, out var maxVal);

                var maxBrightness = (supportsDdc && maxVal > 0) ? (int)maxVal : 100;

                _monitors.Add(new DdcMonitor
                {
                    DeviceName = deviceName,
                    FriendlyName = friendlyName,
                    Description = pm.szPhysicalMonitorDescription?.Trim() ?? deviceName,
                    ResolutionWidth = resW,
                    ResolutionHeight = resH,
                    SupportsDdcCi = supportsDdc,
                    MaxDdcBrightness = maxBrightness,
                    LastCommandedPercent = supportsDdc ? (int)Math.Round(current * 100.0 / maxBrightness) : -1,
                    Handle = pm.hPhysicalMonitor,
                    Group = group
                });
            }
        }
    }

    private static string GetDeviceName(IntPtr hMonitor, out int resW, out int resH)
    {
        resW = 0; resH = 0;
        var mi = new NativeMethods.MONITORINFOEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
        };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            return $"Monitor_0x{hMonitor:X}";

        resW = mi.rcMonitor.Right  - mi.rcMonitor.Left;
        resH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        return mi.szDevice;
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
        lock (_lock)
            DisposeGroups();
    }
}
