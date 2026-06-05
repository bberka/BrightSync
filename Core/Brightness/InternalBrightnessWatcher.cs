using System.Management;
using Serilog;

namespace BrightSync.Core.Brightness;

/// <summary>
/// Helper utility to read/write internal brightness via WMI.
/// </summary>
public sealed class InternalBrightnessWatcher : IDisposable
{
#pragma warning disable CS0067
    // Keeping this event for backward compatibility (no-op)
    public event EventHandler<int>? BrightnessChanged;
#pragma warning restore CS0067

    public void Start()
    {
        Log.Information("WMI internal brightness helper initialized");
    }

    /// <summary>Reads current internal brightness from WMI.</summary>
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

    public void Dispose()
    {
        // No-op for compatibility
    }
}
