using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using BrightSync.Core.Interop;
using Serilog;

namespace BrightSync.Core.Monitors;

internal static class DisplaySettingsService
{
    public static List<int> GetSupportedRefreshRates(string deviceName)
    {
        var rates = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return new List<int> { 60 };
        }

        try
        {
            var devMode = new NativeMethods.DEVMODE();
            devMode.dmSize = (ushort)Marshal.SizeOf(typeof(NativeMethods.DEVMODE));
            int modeNum = 0;

            while (NativeMethods.EnumDisplaySettings(deviceName, modeNum, ref devMode))
            {
                if (devMode.dmDisplayFrequency > 0)
                {
                    rates.Add((int)devMode.dmDisplayFrequency);
                }
                modeNum++;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enumerate display settings for device {DeviceName}", deviceName);
        }

        return rates.OrderBy(r => r).ToList();
    }

    public static int GetCurrentRefreshRate(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return 60;
        }

        try
        {
            var devMode = new NativeMethods.DEVMODE();
            devMode.dmSize = (ushort)Marshal.SizeOf(typeof(NativeMethods.DEVMODE));
            if (NativeMethods.EnumDisplaySettings(deviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref devMode))
            {
                return (int)devMode.dmDisplayFrequency;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get current display settings for device {DeviceName}", deviceName);
        }

        return 60;
    }

    public static bool SetRefreshRate(string deviceName, int refreshRate)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return false;
        }

        try
        {
            var devMode = new NativeMethods.DEVMODE();
            devMode.dmSize = (ushort)Marshal.SizeOf(typeof(NativeMethods.DEVMODE));
            if (NativeMethods.EnumDisplaySettings(deviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref devMode))
            {
                if (devMode.dmDisplayFrequency == refreshRate)
                {
                    return true;
                }

                devMode.dmDisplayFrequency = (uint)refreshRate;
                devMode.dmFields = NativeMethods.DM_DISPLAYFREQUENCY;

                int result = NativeMethods.ChangeDisplaySettingsEx(deviceName, ref devMode, IntPtr.Zero, NativeMethods.CDS_UPDATEREGISTRY, IntPtr.Zero);
                if (result == NativeMethods.DISP_CHANGE_SUCCESSFUL)
                {
                    Log.Information("Successfully changed refresh rate of device {DeviceName} to {RefreshRate}Hz", deviceName, refreshRate);
                    return true;
                }
                else
                {
                    Log.Warning("Failed to change refresh rate of device {DeviceName} to {RefreshRate}Hz. Result code: {Result}", deviceName, refreshRate, result);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to set display settings for device {DeviceName} to {RefreshRate}Hz", deviceName, refreshRate);
        }

        return false;
    }
}
