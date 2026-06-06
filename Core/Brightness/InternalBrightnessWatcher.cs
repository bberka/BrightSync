using WmiLight;
using Serilog;

namespace BrightSync.Core.Brightness;

/// <summary>
/// Helper utility to read/write internal brightness via WMI (AOT compatible).
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
            using var connection = new WmiConnection(@"\\.\root\WMI");
            foreach (var obj in connection.CreateQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"))
            {
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
            using var connection = new WmiConnection(@"\\.\root\WMI");
            var updated = false;

            foreach (var obj in connection.CreateQuery("SELECT * FROM WmiMonitorBrightnessMethods"))
            {
                using var method = obj.GetMethod("WmiSetBrightness");
                using var inParams = method.CreateInParameters();
                // WmiLight's UInt32 setter can fail on WmiSetBrightness input objects
                // with WBEM_E_FAILED on some systems. WMI accepts Int32 here and
                // coerces it to the method's UInt32 Timeout parameter.
                inParams.SetPropertyValue("Timeout", 0);
                inParams.SetPropertyValue("Brightness", (byte)brightness);

                obj.ExecuteMethod(method, inParams, out _);
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
