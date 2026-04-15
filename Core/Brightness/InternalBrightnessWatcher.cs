using System.Management;
using Serilog;

namespace BrightSync.Core.Brightness;

/// <summary>
/// Watches for Windows internal display brightness changes via WMI.
/// Fires <see cref="BrightnessChanged"/> whenever the built-in slider moves.
/// Falls back to polling every 500 ms if event registration fails.
/// </summary>
public sealed class InternalBrightnessWatcher : IDisposable
{
    public event EventHandler<int>? BrightnessChanged;

    private ManagementEventWatcher? _eventWatcher;
    private System.Timers.Timer? _pollTimer;
    private int _lastBrightness = -1;
    private bool _disposed;

    /// <summary>Reads current internal brightness without starting the watcher.</summary>
    public int ReadCurrentBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI",
                "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var o in searcher.Get())
            {
                var obj = (ManagementObject)o;
                return Convert.ToInt32(obj["CurrentBrightness"]);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Current internal brightness could not be read from WMI");
        }

        return -1;
    }

    /// <summary>Starts watching for brightness changes.</summary>
    public void Start()
    {
        _lastBrightness = ReadCurrentBrightness();
        if (TryStartEventWatcher())
        {
            Log.Information("Internal brightness watcher started in WMI event mode");
            return;
        }
        // Fallback: poll every 500 ms
        StartPolling();
        Log.Warning("Internal brightness watcher fell back to polling mode");
    }

    /// <summary>Sets the internal display brightness via WMI.</summary>
    public bool TrySetBrightness(int brightness)
    {
        brightness = Math.Clamp(brightness, 0, 100);

        try
        {
            var scope = new ManagementScope(@"root\WMI");
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));

            var updated = false;
            foreach (var o in searcher.Get())
            {
                var obj = (ManagementObject)o;
                obj.InvokeMethod("WmiSetBrightness", [uint.MinValue, (byte)brightness]);
                updated = true;
            }

            if (updated)
            {
                FireIfChanged(brightness);
                Log.Debug("Internal brightness set through WMI to {Brightness}%", brightness);
            }
            else
            {
                Log.Warning("No WMI brightness targets were available for internal brightness update");
            }

            return updated;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set internal brightness through WMI");
            return false;
        }
    }

    private bool TryStartEventWatcher()
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            // WITHIN 1 = poll interval for the WMI infrastructure (seconds)
            var query = new WqlEventQuery(
                "SELECT * FROM __InstanceModificationEvent WITHIN 1 " +
                "WHERE TargetInstance ISA 'WmiMonitorBrightness'");

            _eventWatcher = new ManagementEventWatcher(scope, query);
            _eventWatcher.EventArrived += OnWmiEvent;
            _eventWatcher.Start();
            return true;
        }
        catch (Exception ex)
        {
            _eventWatcher?.Dispose();
            _eventWatcher = null;
            Log.Warning(ex, "Failed to start WMI event watcher for internal brightness");
            return false;
        }
    }

    private void OnWmiEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            if (e.NewEvent["TargetInstance"] is ManagementBaseObject target)
            {
                var brightness = Convert.ToInt32(target["CurrentBrightness"]);
                FireIfChanged(brightness);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Ignoring malformed internal brightness event");
        }
    }

    private void StartPolling()
    {
        _pollTimer = new System.Timers.Timer(500);
        _pollTimer.Elapsed += (_, _) =>
        {
            var b = ReadCurrentBrightness();
            if (b >= 0) FireIfChanged(b);
        };
        _pollTimer.Start();
        Log.Debug("Internal brightness polling timer started");
    }

    private void FireIfChanged(int brightness)
    {
        if (brightness == _lastBrightness) return;
        _lastBrightness = brightness;
        BrightnessChanged?.Invoke(this, brightness);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Debug("Disposing internal brightness watcher");
        _eventWatcher?.Stop();
        _eventWatcher?.Dispose();
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
    }
}
