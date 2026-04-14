using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using BrightSync.Core.Interop;

namespace BrightSync.Core.Monitors;

/// <summary>
/// Resolves actual monitor brand/model names from WMI WmiMonitorID,
/// matched via EnumDisplayDevices hardware IDs.
/// Falls back gracefully when WMI is unavailable.
/// </summary>
public static class MonitorNameResolver
{
    // hwId (e.g. "DEL4141") → friendly name from WMI
    private static Dictionary<string, string>? _wmiCache;
    private static readonly object _cacheLock = new();

    /// <summary>
    /// Returns a friendly display name for the monitor attached to <paramref name="adapterDeviceName"/>
    /// (e.g. "\\.\DISPLAY1").
    /// </summary>
    public static string Resolve(string adapterDeviceName)
    {
        string hwId = GetHardwareId(adapterDeviceName);
        if (string.IsNullOrEmpty(hwId)) return "Unknown Monitor";

        EnsureCache();

        if (_wmiCache!.TryGetValue(hwId, out string? friendly) && !string.IsNullOrWhiteSpace(friendly))
            return friendly;

        // Fallback: decode the PnP ID itself (first 3 chars = EISA manufacturer)
        return DecodePnpId(hwId);
    }

    // --- Private ---

    /// <summary>
    /// Uses EnumDisplayDevices to get the hardware ID string (e.g. "DEL4141") for a given
    /// adapter device name.
    /// </summary>
    private static string GetHardwareId(string adapterDeviceName)
    {
        var dd = new NativeMethods.DISPLAY_DEVICE
        {
            cb = (uint)Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>()
        };

        // Enumerate monitors attached to this adapter (index 0 = first monitor)
        if (!NativeMethods.EnumDisplayDevices(adapterDeviceName, 0, ref dd, 0))
            return string.Empty;

        // DeviceID looks like "MONITOR\DEL4141\{4D36E96E-...}\0002"
        string[] parts = dd.DeviceID.Split('\\');
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    private static void EnsureCache()
    {
        lock (_cacheLock)
        {
            if (_wmiCache != null) return;
            _wmiCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT * FROM WmiMonitorID");

                foreach (ManagementObject obj in searcher.Get())
                {
                    string instanceName = obj["InstanceName"]?.ToString() ?? string.Empty;
                    // InstanceName: "DISPLAY\DEL4141\5&abc&0&UID257_0"
                    string[] parts = instanceName.Split('\\');
                    if (parts.Length < 2) continue;

                    string hwId = parts[1];
                    string friendly = DecodeUshorts(obj["UserFriendlyName"] as ushort[]);

                    if (!string.IsNullOrWhiteSpace(friendly))
                        _wmiCache[hwId] = friendly;
                }
            }
            catch { /* WMI unavailable on some configurations */ }
        }
    }

    private static string DecodeUshorts(ushort[]? arr)
    {
        if (arr == null) return string.Empty;
        var sb = new StringBuilder(arr.Length);
        foreach (ushort c in arr)
        {
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Decodes a PnP vendor ID (first 3 chars of HW ID) to manufacturer name.
    /// The remaining chars are the model hex code — kept as-is.
    /// </summary>
    private static string DecodePnpId(string hwId)
    {
        if (hwId.Length < 3) return hwId;

        // 3-char EISA/PnP vendor codes (ISA Plug and Play standard)
        string vendor = hwId[..3].ToUpperInvariant();
        string model = hwId.Length > 3 ? hwId[3..] : string.Empty;

        string mfrName = vendor switch
        {
            "ACR" => "Acer",
            "ACI" => "Asus",
            "APP" => "Apple",
            "DEL" => "Dell",
            "EIZ" => "EIZO",
            "GSM" => "LG",
            "HPN" or "HWP" => "HP",
            "HIC" => "Hisense",
            "HSD" => "HannStar",
            "IBM" => "IBM",
            "LEN" => "Lenovo",
            "MAX" => "Maxdata",
            "MEI" => "Panasonic",
            "MSI" => "MSI",
            "NEC" => "NEC",
            "PHL" => "Philips",
            "SAM" => "Samsung",
            "SHP" => "Sharp",
            "SNY" => "Sony",
            "VSC" => "ViewSonic",
            "BNQ" => "BenQ",
            "AOC" => "AOC",
            _ => vendor
        };

        return string.IsNullOrEmpty(model) ? mfrName : $"{mfrName} {model}";
    }

    /// <summary>Clears the WMI cache so the next call re-queries (useful after Refresh).</summary>
    public static void InvalidateCache()
    {
        lock (_cacheLock)
            _wmiCache = null;
    }
}
