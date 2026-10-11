using WmiLight;
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
    // hwId (e.g. "DEL4141") → identity from WMI
    private static Dictionary<string, MonitorIdentity>? _wmiCache;
    private static readonly Lock CacheLock = new();

    /// <summary>
    /// Returns a friendly display name for the monitor attached to <paramref name="adapterDeviceName"/>
    /// (e.g. "\\.\DISPLAY1").
    /// </summary>
    public static string Resolve(string adapterDeviceName)
        => ResolveIdentity(adapterDeviceName).FriendlyName;

    public static MonitorIdentity ResolveIdentity(string adapterDeviceName)
    {
        var hwId = GetHardwareId(adapterDeviceName);
        if (string.IsNullOrEmpty(hwId)) return MonitorIdentity.Unknown;

        EnsureCache();

        if (_wmiCache!.TryGetValue(hwId, out var identity) && !string.IsNullOrWhiteSpace(identity.FriendlyName))
            return identity;

        // Fallback: decode the PnP ID itself (first 3 chars = EISA manufacturer)
        return MonitorNames.DecodePnpId(hwId);
    }

    internal static string GetHardwareIdForAdapter(string adapterDeviceName)
        => GetHardwareId(adapterDeviceName);

    internal static string GetDeviceIdForAdapter(string adapterDeviceName)
        => GetDeviceId(adapterDeviceName);

    internal static MonitorIdentity DecodeHardwareId(string hardwareId)
        => MonitorNames.DecodePnpId(hardwareId);

    internal static MonitorIdentity BuildIdentityFromParts(string manufacturer, string model)
        => MonitorNames.BuildIdentity(manufacturer, model);

    // --- Private ---

    /// <summary>
    /// Uses EnumDisplayDevices to get the hardware ID string (e.g. "DEL4141") for a given
    /// adapter device name.
    /// </summary>
    private static string GetHardwareId(string adapterDeviceName)
    {
        var deviceId = GetDeviceId(adapterDeviceName);
        var parts = deviceId.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    private static string GetDeviceId(string adapterDeviceName)
    {
        var dd = new NativeMethods.DISPLAY_DEVICE
        {
            cb = (uint)Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>()
        };

        // Enumerate monitors attached to this adapter (index 0 = first monitor)
        if (!NativeMethods.EnumDisplayDevices(adapterDeviceName, 0, ref dd, 0))
            return string.Empty;

        // DeviceID looks like "MONITOR\DEL4141\{4D36E96E-...}\0002".
        // Keep the complete value for profile identity; the shorter hardware ID
        // remains available for the existing WMI name/connection fallbacks.
        return dd.DeviceID?.Trim() ?? string.Empty;
    }

    private static void EnsureCache()
    {
        lock (CacheLock)
        {
            if (_wmiCache != null) return;
            _wmiCache = new Dictionary<string, MonitorIdentity>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var connection = new WmiConnection(@"\\.\root\WMI");
                foreach (var obj in connection.CreateQuery("SELECT * FROM WmiMonitorID"))
                {
                    var instanceName = obj["InstanceName"]?.ToString() ?? string.Empty;
                    // InstanceName: "DISPLAY\DEL4141\5&abc&0&UID257_0"
                    var parts = instanceName.Split('\\');
                    if (parts.Length < 2) continue;

                    var hwId = parts[1];
                    var model = DecodeUshorts(ConvertToUshortArray(obj["UserFriendlyName"]));
                    var manufacturerCode = DecodeUshorts(ConvertToUshortArray(obj["ManufacturerName"]));
                    var manufacturer = MonitorNames.DecodeManufacturerCode(manufacturerCode);

                    var identity = MonitorNames.BuildIdentity(manufacturer, model);
                    if (!string.IsNullOrWhiteSpace(identity.FriendlyName))
                        _wmiCache[hwId] = identity;
                }
            }
            catch
            {
                /* WMI unavailable on some configurations */
            }
        }
    }

    private static ushort[]? ConvertToUshortArray(object? value)
    {
        if (value == null) return null;
        if (value is ushort[] ushorts) return ushorts;
        if (value is int[] ints) return ints.Select(i => (ushort)i).ToArray();
        if (value is object[] objects) return objects.Select(o => Convert.ToUInt16(o)).ToArray();
        if (value is Array array)
        {
            var result = new ushort[array.Length];
            for (int i = 0; i < array.Length; i++)
            {
                result[i] = Convert.ToUInt16(array.GetValue(i));
            }

            return result;
        }

        return null;
    }

    private static string DecodeUshorts(ushort[]? arr)
    {
        if (arr == null) return string.Empty;
        var sb = new StringBuilder(arr.Length);
        foreach (var c in arr)
        {
            if (c == 0) break;
            sb.Append((char)c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>Clears the WMI cache so the next call re-queries (useful after Refresh).</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock)
            _wmiCache = null;
    }
}
