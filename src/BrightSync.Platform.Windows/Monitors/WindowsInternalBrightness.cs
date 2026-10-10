using BrightSync.Platform;
using Serilog;
using WmiLight;

namespace BrightSync.Core.Monitors;

/// <summary>Reads and writes the internal panel brightness through WMI (AOT compatible).</summary>
internal sealed class WindowsInternalBrightness : IInternalBrightness
{
    public int ReadCurrentBrightness()
    {
        try
        {
            using var connection = new WmiConnection(@"\.\root\WMI");
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

    public bool TrySetBrightness(int brightness)
    {
        brightness = Math.Clamp(brightness, 0, 100);

        try
        {
            using var connection = new WmiConnection(@"\.\root\WMI");
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
                Log.Debug("Internal brightness set through WMI to {Brightness}%", brightness);
            else
                Log.Warning("No WMI brightness targets were available for internal brightness update");

            return updated;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set internal brightness through WMI");
            return false;
        }
    }
}
