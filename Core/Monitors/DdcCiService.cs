using System.Runtime.InteropServices;
using BrightnessSync.Core.Interop;

namespace BrightnessSync.Core.Monitors;

/// <summary>
/// Represents a DDC/CI-capable external monitor.
/// </summary>
public sealed class DdcMonitor
{
    /// <summary>Stable identifier — Windows device name, e.g. \\.\DISPLAY2</summary>
    public string DeviceName { get; init; } = string.Empty;
    /// <summary>Friendly name resolved from WMI (brand + model), e.g. "LG 27GP950-B".</summary>
    public string FriendlyName { get; init; } = string.Empty;
    /// <summary>Raw firmware description from the DDC/CI physical monitor struct.</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>Horizontal resolution in pixels.</summary>
    public int ResolutionWidth { get; init; }
    /// <summary>Vertical resolution in pixels.</summary>
    public int ResolutionHeight { get; init; }
    /// <summary>Whether the monitor responded to a DDC/CI brightness query.</summary>
    public bool SupportsDdcCi { get; init; }
    /// <summary>Maximum DDC/CI brightness value (usually 100, but can vary).</summary>
    public int MaxDdcBrightness { get; init; } = 100;
    /// <summary>Last brightness value we commanded, in percent (0–100). -1 = unknown.</summary>
    public int LastCommandedPercent { get; set; } = -1;

    internal IntPtr Handle { get; init; }
    // Owning group — needed for DestroyPhysicalMonitors cleanup
    internal PhysicalMonitorGroup? Group { get; init; }
}

/// <summary>
/// Holds a PHYSICAL_MONITOR array obtained from a single HMONITOR so it can be
/// properly destroyed via <see cref="NativeMethods.DestroyPhysicalMonitors"/>.
/// </summary>
internal sealed class PhysicalMonitorGroup : IDisposable
{
    private bool _disposed;
    public NativeMethods.PHYSICAL_MONITOR[] Monitors { get; }
    public string DeviceName { get; }

    public PhysicalMonitorGroup(NativeMethods.PHYSICAL_MONITOR[] monitors, string deviceName)
    {
        Monitors = monitors;
        DeviceName = deviceName;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            NativeMethods.DestroyPhysicalMonitors((uint)Monitors.Length, Monitors);
        }
        catch { /* best-effort */ }
    }
}

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
        uint ddcValue = (uint)Math.Round(brightnessPercent / 100.0 * monitor.MaxDdcBrightness);
        // Acquire the lock so a concurrent Refresh() cannot destroy the handle mid-call.
        lock (_lock)
        {
            if (_disposed) return false;
            bool ok = NativeMethods.SetVCPFeature(monitor.Handle, NativeMethods.VCP_BRIGHTNESS, ddcValue);
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
            bool ok = NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                monitor.Handle, NativeMethods.VCP_BRIGHTNESS,
                out _, out uint current, out uint max);
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
            string deviceName = GetDeviceName(hMonitor, out int resW, out int resH);

            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
                continue;

            var physicals = new NativeMethods.PHYSICAL_MONITOR[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals))
                continue;

            var group = new PhysicalMonitorGroup(physicals, deviceName);
            _groups.Add(group);

            // Resolve friendly name once per HMONITOR (one monitor per adapter port)
            string friendlyName = MonitorNameResolver.Resolve(deviceName);

            for (int i = 0; i < physicals.Length; i++)
            {
                var pm = physicals[i];
                bool supportsDdc = NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                    pm.hPhysicalMonitor, NativeMethods.VCP_BRIGHTNESS,
                    out _, out uint current, out uint maxVal);

                int maxBrightness = (supportsDdc && maxVal > 0) ? (int)maxVal : 100;

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
