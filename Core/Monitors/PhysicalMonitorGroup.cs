using BrightSync.Core.Interop;

namespace BrightSync.Core.Monitors;

/// <summary>
/// Holds a PHYSICAL_MONITOR array obtained from a single HMONITOR so it can be
/// properly destroyed via <see cref="System.Windows.Forms.NativeMethods.DestroyPhysicalMonitors"/>.
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