namespace BrightSync.Core.Monitors;

/// <summary>
/// Result of one monitor enumeration pass. Owns the backend resources attached to its monitors
/// until the set is replaced and <see cref="DisposeResources"/> is called.
/// </summary>
public sealed class DdcMonitorSet
{
    public IReadOnlyList<DdcMonitor> Monitors { get; }
    public IReadOnlyList<IDisposable> Resources { get; }

    public DdcMonitorSet(IEnumerable<DdcMonitor> monitors)
        : this(monitors, [])
    {
    }

    public DdcMonitorSet(
        IEnumerable<DdcMonitor> monitors,
        IEnumerable<IDisposable> resources)
    {
        Monitors = monitors.ToList();
        Resources = resources.ToList();
    }

    public void DisposeResources()
    {
        foreach (var resource in Resources)
        {
            try
            {
                resource.Dispose();
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }
}
