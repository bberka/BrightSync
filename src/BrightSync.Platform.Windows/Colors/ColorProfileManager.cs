using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using BrightSync.Core.Interop;
using Serilog;

namespace BrightSync.Core.Colors;

internal static class ColorProfileManager
{
    public static List<string> GetInstalledColorProfiles()
    {
        try
        {
            uint size = 0;
            NativeMethods.GetColorProfileDirectory(null, null, ref size);
            if (size > 0)
            {
                var sb = new StringBuilder((int)size);
                if (NativeMethods.GetColorProfileDirectory(null, sb, ref size))
                {
                    var dir = sb.ToString();
                    if (Directory.Exists(dir))
                    {
                        var profiles = Directory.GetFiles(dir, "*.*")
                            .Where(f => f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase))
                            .Select(Path.GetFileName)
                            .Where(f => !string.IsNullOrEmpty(f))
                            .Select(f => f!)
                            .OrderBy(f => f)
                            .ToList();
                        return profiles;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get installed color profiles directory");
        }
        return new List<string>();
    }

    public static string GetActiveColorProfile(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return string.Empty;
        try
        {
            uint size = 260;
            byte[] buffer = new byte[size * 2]; // Unicode string buffer
            if (NativeMethods.WcsGetDefaultColorProfile(1, deviceName, 0, 3, 0, size * 2, buffer))
            {
                var name = Encoding.Unicode.GetString(buffer).Split('\0')[0];
                return Path.GetFileName(name);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to query active color profile via WcsGetDefaultColorProfile for device {DeviceName}", deviceName);
        }
        return string.Empty;
    }

    public static bool SetActiveColorProfile(string deviceName, string profileName)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || string.IsNullOrWhiteSpace(profileName)) return false;
        try
        {
            var filename = Path.GetFileName(profileName);

            // Link the profile with the device first
            NativeMethods.AssociateColorProfileWithDevice(null, filename, deviceName);

            // Set as the default profile for current user
            bool setSuccess = NativeMethods.WcsSetDefaultColorProfile(1, deviceName, 0, 3, 0, filename);
            if (setSuccess)
            {
                Log.Information("Successfully set default color profile for device {Device} to {Profile}", deviceName, filename);
                return true;
            }
            else
            {
                Log.Warning("WcsSetDefaultColorProfile failed for device {Device} with profile {Profile}. Error={Error}", deviceName, filename, Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to set active color profile for device {DeviceName} to {ProfileName}", deviceName, profileName);
        }
        return false;
    }
}
