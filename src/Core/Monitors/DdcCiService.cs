using System.Runtime.InteropServices;
using BrightSync.Core.Config;
using BrightSync.Core.Interop;
using Serilog;

namespace BrightSync.Core.Monitors;

/// <summary>
/// Enumerates physical monitors and exposes DDC/CI brightness get/set operations.
/// Thread-safe for concurrent brightness writes from the sync engine.
/// </summary>
public sealed class DdcCiService : IDisposable
{
    private const int RetryDelayMilliseconds = 60;

    private readonly ConfigManager _config;
    private readonly object _lock = new();
    private List<DdcMonitor> _monitors = new();
    private List<PhysicalMonitorGroup> _groups = new();
    private bool _disposed;

    public DdcCiService(ConfigManager config)
    {
        _config = config;
        Refresh();
    }

    /// <summary>Re-enumerates all connected monitors. Call after display topology changes.</summary>
    public void Refresh()
    {
        MonitorNameResolver.InvalidateCache();
        lock (_lock)
        {
            var useLegacyDetection = _config.Config.UseLegacyDdcCiDetection;
            DisposeGroups();
            _groups = new List<PhysicalMonitorGroup>();
            _monitors = new List<DdcMonitor>();
            EnumerateMonitors(useLegacyDetection);
            Log.Information(
                "DDC/CI refresh finished. DetectionMode={DetectionMode}, TotalMonitors={TotalMonitors}, ControllableMonitors={ControllableMonitors}",
                useLegacyDetection ? "Legacy" : "Modern",
                _monitors.Count,
                _monitors.Count(m => m.SupportsDdcCi));
        }
    }

    /// <summary>Returns a snapshot of currently known DDC/CI monitors.</summary>
    public IReadOnlyList<DdcMonitor> GetMonitors()
    {
        lock (_lock)
            return _monitors.ToList();
    }

    /// <summary>
    /// Sets brightness on a specific monitor. brightness is a percentage 0–100.
    /// Maps the percentage to the monitor's native DDC range.
    /// </summary>
    public bool SetBrightness(DdcMonitor monitor, int brightnessPercent)
    {
        brightnessPercent = Math.Clamp(brightnessPercent, 0, 100);
        // Acquire the lock so a concurrent Refresh() cannot destroy the handle mid-call.
        lock (_lock)
        {
            if (_disposed) return false;
            var ok = monitor.BrightnessBackendType switch
            {
                MonitorBrightnessBackend.HighLevelApi => TrySetHighLevelBrightness(monitor, brightnessPercent),
                MonitorBrightnessBackend.WriteOnlyDdcCi => TrySetVcpBrightness(monitor, brightnessPercent,
                    retryCount: 2),
                MonitorBrightnessBackend.LowLevelDdcCi =>
                    TrySetVcpBrightness(monitor, brightnessPercent, retryCount: 2),
                MonitorBrightnessBackend.InternalWmi => TrySetInternalWmiBrightness(brightnessPercent),
                _ => false
            };

            if (ok)
            {
                monitor.LastCommandedPercent = brightnessPercent;
                Log.Debug("Set monitor brightness. Monitor={Monitor}, Brightness={Brightness}%, Backend={Backend}",
                    monitor.FriendlyName, brightnessPercent, monitor.BrightnessBackend);
            }
            else
            {
                Log.Warning("Brightness update failed. Monitor={Monitor}, Brightness={Brightness}%, Backend={Backend}",
                    monitor.FriendlyName, brightnessPercent, monitor.BrightnessBackend);
            }

            return ok;
        }
    }

    /// <summary>
    /// Reads current brightness from the monitor via DDC/CI.
    /// This call is slow (~40–150 ms per monitor) — avoid on the UI thread.
    /// </summary>
    public bool TryGetBrightness(DdcMonitor monitor, out int brightnessPercent)
    {
        brightnessPercent = 0;
        lock (_lock)
        {
            if (_disposed) return false;
            var ok = monitor.BrightnessBackendType switch
            {
                MonitorBrightnessBackend.HighLevelApi => TryGetHighLevelBrightness(monitor, out brightnessPercent),
                MonitorBrightnessBackend.LowLevelDdcCi => TryGetVcpBrightness(monitor, out brightnessPercent,
                    retryCount: 2),
                MonitorBrightnessBackend.InternalWmi => TryGetInternalWmiBrightness(out brightnessPercent),
                _ => false
            };

            if (!ok)
            {
                Log.Debug("Unable to read current brightness for monitor {Monitor} using backend {Backend}",
                    monitor.FriendlyName,
                    monitor.BrightnessBackend);
            }

            return ok;
        }
    }

    // --- Private ---

    private void EnumerateMonitors(bool useLegacyDetection)
    {
        var hMonitors = new List<IntPtr>();
        NativeMethods.EnumDisplayMonitors(
            IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                hMonitors.Add(hMon);
                return true;
            },
            IntPtr.Zero);

        foreach (var hMonitor in hMonitors)
        {
            var deviceName = GetDeviceName(hMonitor, out var resW, out var resH);

            var internalDetection = MonitorDetectionResolver.Resolve(deviceName, deviceName, useLegacyDetection);
            if (internalDetection.IsInternal)
            {
                var internalHdrInfo = internalDetection.HdrInfo;
                var tempWatcher = new BrightSync.Core.Brightness.InternalBrightnessWatcher();
                var currentBrightness = tempWatcher.ReadCurrentBrightness();

                _monitors.Add(new DdcMonitor
                {
                    DeviceName = deviceName,
                    ManufacturerName = internalDetection.ManufacturerName,
                    ModelName = internalDetection.ModelName,
                    FriendlyName = internalDetection.FriendlyName,
                    Description = "Internal Display",
                    ResolutionWidth = resW,
                    ResolutionHeight = resH,
                    RefreshRateHz = useLegacyDetection ? 0 : GetRefreshRate(deviceName),
                    ConnectionType = internalDetection.ConnectionType,
                    IsInternal = true,
                    SupportsDdcCi = true,
                    SupportsBrightnessRead = true,
                    MinNativeBrightness = 0,
                    MaxDdcBrightness = 100,
                    LastCommandedPercent = currentBrightness >= 0 ? currentBrightness : 50,
                    BrightnessBackendType = MonitorBrightnessBackend.InternalWmi,
                    BrightnessBackend = "WMI (Internal)",
                    IsHdrSupported = internalHdrInfo.IsHdrSupported,
                    IsHdrEnabled = internalHdrInfo.IsHdrEnabled,
                    SdrWhiteLevelNits = internalHdrInfo.SdrWhiteLevelNits,
                    IsAppleDisplay = false,
                    IsAppleStudioDisplay = false,
                    DetectionBackend = $"{internalDetection.DetectionBackend} + WMI",
                    DetectionDetails = "Internal display controlled via WMI (WmiSetBrightness).",
                    Handle = IntPtr.Zero,
                    Group = null
                });

                Log.Debug(
                    "Detected internal monitor. Device={DeviceName}, FriendlyName={FriendlyName}, SupportsDdcCi=True, IsInternal=True, BrightnessBackend=WMI",
                    deviceName,
                    internalDetection.FriendlyName);

                continue;
            }

            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out var count) || count == 0)
            {
                Log.Debug("No physical monitors found for HMONITOR {Handle}", hMonitor);
                continue;
            }

            var physicals = new NativeMethods.PHYSICAL_MONITOR[count];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals))
            {
                Log.Warning("Failed to resolve physical monitor handles for device {DeviceName}", deviceName);
                continue;
            }

            var group = new PhysicalMonitorGroup(physicals, deviceName);
            _groups.Add(group);
            var primaryDescription = physicals[0].szPhysicalMonitorDescription?.Trim() ?? deviceName;
            var detection = MonitorDetectionResolver.Resolve(deviceName, primaryDescription, useLegacyDetection);
            var hdrInfo = detection.HdrInfo;
            var isAppleDisplay =
                string.Equals(detection.ManufacturerName, "Apple", StringComparison.OrdinalIgnoreCase) ||
                detection.FriendlyName.Contains("Apple", StringComparison.OrdinalIgnoreCase);
            var isAppleStudioDisplay =
                detection.FriendlyName.Contains("Studio Display", StringComparison.OrdinalIgnoreCase) ||
                detection.ModelName.Contains("Studio Display", StringComparison.OrdinalIgnoreCase);

            for (var i = 0; i < physicals.Length; i++)
            {
                var pm = physicals[i];
                var description = pm.szPhysicalMonitorDescription?.Trim() ?? deviceName;
                var brightnessSupport = MonitorBrightnessResolver.Probe(pm.hPhysicalMonitor);
                var details = BuildCombinedDetectionDetails(
                    detection.DetectionDetails,
                    brightnessSupport.DetectionDetails,
                    hdrInfo,
                    isAppleStudioDisplay,
                    brightnessSupport.SupportsBrightnessControl);

                var m = new DdcMonitor
                {
                    DeviceName = deviceName,
                    ManufacturerName = detection.ManufacturerName,
                    ModelName = detection.ModelName,
                    FriendlyName = detection.FriendlyName,
                    Description = description,
                    ResolutionWidth = resW,
                    ResolutionHeight = resH,
                    RefreshRateHz = useLegacyDetection ? 0 : GetRefreshRate(deviceName),
                    ConnectionType = detection.ConnectionType,
                    IsInternal = detection.IsInternal,
                    SupportsDdcCi = brightnessSupport.SupportsBrightnessControl,
                    SupportsBrightnessRead = brightnessSupport.SupportsBrightnessRead,
                    MinNativeBrightness = (int)brightnessSupport.MinimumNativeBrightness,
                    MaxDdcBrightness = (int)brightnessSupport.MaximumNativeBrightness,
                    LastCommandedPercent = brightnessSupport.CurrentBrightnessPercent,
                    BrightnessBackendType = brightnessSupport.Backend,
                    BrightnessBackend = brightnessSupport.BackendLabel,
                    IsHdrSupported = hdrInfo.IsHdrSupported,
                    IsHdrEnabled = hdrInfo.IsHdrEnabled,
                    SdrWhiteLevelNits = hdrInfo.SdrWhiteLevelNits,
                    IsAppleDisplay = isAppleDisplay,
                    IsAppleStudioDisplay = isAppleStudioDisplay,
                    DetectionBackend = $"{detection.DetectionBackend} + {brightnessSupport.BackendLabel}",
                    DetectionDetails = details,
                    Handle = pm.hPhysicalMonitor,
                    Group = group
                };

                ProbeAdvancedCapabilities(m);
                _monitors.Add(m);

                Log.Debug(
                    "Detected monitor. DetectionMode={DetectionMode}, Device={DeviceName}, FriendlyName={FriendlyName}, SupportsDdcCi={SupportsDdcCi}, IsInternal={IsInternal}, DetectionBackend={DetectionBackend}, BrightnessBackend={BrightnessBackend}, HdrEnabled={HdrEnabled}",
                    useLegacyDetection ? "Legacy" : "Modern",
                    deviceName,
                    detection.FriendlyName,
                    brightnessSupport.SupportsBrightnessControl,
                    detection.IsInternal,
                    detection.DetectionBackend,
                    brightnessSupport.BackendLabel,
                    hdrInfo.IsHdrEnabled);
            }
        }
    }

    private static string BuildCombinedDetectionDetails(
        string detectionDetails,
        string brightnessDetails,
        HdrDisplayInfo hdrInfo,
        bool isAppleStudioDisplay,
        bool supportsBrightnessControl)
    {
        var parts = new List<string>
        {
            detectionDetails,
            brightnessDetails
        };

        if (hdrInfo.IsHdrSupported)
        {
            var hdrText = hdrInfo.IsHdrEnabled
                ? "HDR is currently enabled."
                : "HDR is supported but currently disabled.";
            if (hdrInfo.SdrWhiteLevelNits > 0)
                hdrText += $" SDR white level is {hdrInfo.SdrWhiteLevelNits} nits.";
            parts.Add(hdrText);
        }

        if (isAppleStudioDisplay)
        {
            parts.Add(supportsBrightnessControl
                ? "Apple Studio Display was detected and a Windows brightness backend is available on this connection."
                : "Apple Studio Display was detected, but no supported Windows brightness backend was exposed on this connection.");
        }

        return string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    public bool SetVcpFeature(DdcMonitor monitor, byte vcpCode, uint value)
    {
        lock (_lock)
        {
            if (_disposed || monitor.IsInternal) return false;
            
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (NativeMethods.SetVCPFeature(monitor.Handle, vcpCode, value))
                    return true;
                if (attempt < 1)
                    Thread.Sleep(RetryDelayMilliseconds);
            }
            return false;
        }
    }

    public bool GetVcpFeature(DdcMonitor monitor, byte vcpCode, out uint currentValue, out uint maxValue)
    {
        currentValue = 0;
        maxValue = 0;
        lock (_lock)
        {
            if (_disposed || monitor.IsInternal) return false;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                        monitor.Handle,
                        vcpCode,
                        out _,
                        out var current,
                        out var max))
                {
                    currentValue = current;
                    maxValue = max;
                    return true;
                }
                
                var err = Marshal.GetLastWin32Error();
                Log.Debug("GetVcpFeature failed. Monitor={Monitor}, VcpCode=0x{Vcp:X2}, Error=0x{Error:X8}, Attempt={Attempt}", 
                    monitor.FriendlyName, vcpCode, err, attempt + 1);

                if (attempt < 1)
                    Thread.Sleep(RetryDelayMilliseconds);
            }
            return false;
        }
    }

    private string? GetCapabilitiesString(IntPtr hMonitor)
    {
        if (!NativeMethods.GetCapabilitiesStringLength(hMonitor, out var length))
        {
            var err = Marshal.GetLastWin32Error();
            Log.Debug("Failed to get capabilities string length. Error=0x{Error:X8}", err);
            return null;
        }

        if (length == 0) return null;

        var buffer = new byte[length];
        if (!NativeMethods.CapabilitiesRequestAndCapabilitiesReply(hMonitor, buffer, length))
        {
            var err = Marshal.GetLastWin32Error();
            Log.Debug("Failed to get capabilities string. Error=0x{Error:X8}", err);
            return null;
        }

        return System.Text.Encoding.ASCII.GetString(buffer).Trim('\0');
    }

    private static Dictionary<byte, List<uint>> ParseCapabilities(string capString)
    {
        var result = new Dictionary<byte, List<uint>>();
        try
        {
            var vcpIndex = capString.IndexOf("vcp", StringComparison.OrdinalIgnoreCase);
            if (vcpIndex < 0) return result;

            var openParen = capString.IndexOf('(', vcpIndex);
            if (openParen < 0) return result;

            var parenCount = 1;
            var i = openParen + 1;
            var vcpBlock = "";
            for (; i < capString.Length; i++)
            {
                if (capString[i] == '(') parenCount++;
                else if (capString[i] == ')')
                {
                    parenCount--;
                    if (parenCount == 0)
                    {
                        vcpBlock = capString.Substring(openParen + 1, i - openParen - 1);
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(vcpBlock)) return result;

            var index = 0;
            while (index < vcpBlock.Length)
            {
                while (index < vcpBlock.Length && char.IsWhiteSpace(vcpBlock[index]))
                    index++;

                if (index >= vcpBlock.Length) break;

                var tokenStart = index;
                while (index < vcpBlock.Length && char.IsLetterOrDigit(vcpBlock[index]))
                    index++;

                if (index == tokenStart)
                {
                    index++;
                    continue;
                }

                var token = vcpBlock.Substring(tokenStart, index - tokenStart);
                if (byte.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out var vcpCode))
                {
                    var supportedValues = new List<uint>();
                    if (index < vcpBlock.Length && vcpBlock[index] == '(')
                    {
                        var closeIndex = vcpBlock.IndexOf(')', index);
                        if (closeIndex > index)
                        {
                            var valuesStr = vcpBlock.Substring(index + 1, closeIndex - index - 1);
                            var valTokens = valuesStr.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var valToken in valTokens)
                            {
                                if (uint.TryParse(valToken, System.Globalization.NumberStyles.HexNumber, null, out var val))
                                {
                                    supportedValues.Add(val);
                                }
                            }
                            index = closeIndex + 1;
                        }
                    }
                    result[vcpCode] = supportedValues;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to parse capabilities string");
        }

        return result;
    }

    private void ProbeAdvancedCapabilities(DdcMonitor monitor)
    {
        if (monitor.IsInternal)
            return;

        Log.Debug("Probing advanced capabilities for monitor: {Monitor}", monitor.FriendlyName);

        var capStr = GetCapabilitiesString(monitor.Handle);
        Dictionary<byte, List<uint>>? parsedCaps = null;
        if (!string.IsNullOrEmpty(capStr))
        {
            Log.Debug("Capabilities string for {Monitor}: {CapStr}", monitor.FriendlyName, capStr);
            parsedCaps = ParseCapabilities(capStr);
            
            if (parsedCaps.TryGetValue(NativeMethods.VCP_COLOR_PRESET, out var presets))
                monitor.SupportedPresets = presets;

            if (parsedCaps.TryGetValue(NativeMethods.VCP_INPUT_SOURCE, out var inputs))
                monitor.SupportedInputs = inputs;
        }

        if (GetVcpFeature(monitor, NativeMethods.VCP_CONTRAST, out var contrastVal, out var contrastMax))
        {
            monitor.SupportsContrast = true;
            monitor.CurrentContrast = (int)contrastVal;
            monitor.MaxContrast = (int)contrastMax;
        }

        if (GetVcpFeature(monitor, NativeMethods.VCP_VOLUME, out var volVal, out var volMax))
        {
            monitor.SupportsVolume = true;
            monitor.CurrentVolume = (int)volVal;
            monitor.MaxVolume = (int)volMax;
        }

        if (GetVcpFeature(monitor, NativeMethods.VCP_COLOR_PRESET, out var presetVal, out _))
        {
            monitor.SupportsColorPreset = true;
            monitor.CurrentColorPreset = (int)presetVal;
        }

        if (GetVcpFeature(monitor, NativeMethods.VCP_RED_GAIN, out var redVal, out var rgbMax) &&
            GetVcpFeature(monitor, NativeMethods.VCP_GREEN_GAIN, out var greenVal, out _) &&
            GetVcpFeature(monitor, NativeMethods.VCP_BLUE_GAIN, out var blueVal, out _))
        {
            monitor.SupportsRgbGains = true;
            monitor.CurrentRedGain = (int)redVal;
            monitor.CurrentGreenGain = (int)greenVal;
            monitor.CurrentBlueGain = (int)blueVal;
            monitor.MaxRgbGain = (int)rgbMax;
        }

        if (GetVcpFeature(monitor, NativeMethods.VCP_INPUT_SOURCE, out var inputVal, out _))
        {
            monitor.SupportsInputSource = true;
            monitor.CurrentInputSource = (int)inputVal;
        }

        monitor.RawCapabilitiesString = capStr ?? string.Empty;

        if (GetVcpFeature(monitor, NativeMethods.VCP_SHARPNESS, out var sharpnessVal, out var sharpnessMax))
        {
            monitor.SupportsSharpness = true;
            monitor.CurrentSharpness = (int)sharpnessVal;
            monitor.MaxSharpness = (int)sharpnessMax;
        }

        if (GetVcpFeature(monitor, NativeMethods.VCP_SATURATION, out var saturationVal, out var saturationMax))
        {
            monitor.SupportsSaturation = true;
            monitor.CurrentSaturation = (int)saturationVal;
            monitor.MaxSaturation = (int)saturationMax;
        }

        if (GetVcpFeature(monitor, NativeMethods.VCP_GAMMA, out var gammaVal, out _))
        {
            monitor.SupportsGamma = true;
            monitor.CurrentGamma = (int)gammaVal;
        }

        if (GetVcpFeature(monitor, NativeMethods.VCP_POWER_CONTROL, out var powerVal, out _))
        {
            monitor.SupportsPowerControl = true;
            monitor.CurrentPowerState = (int)powerVal;
        }
    }

    private static bool TrySetVcpBrightness(DdcMonitor monitor, int brightnessPercent, int retryCount)
    {
        var ddcValue = (uint)Math.Round(brightnessPercent / 100.0 * monitor.MaxDdcBrightness);
        for (var attempt = 0; attempt < retryCount; attempt++)
        {
            if (NativeMethods.SetVCPFeature(monitor.Handle, NativeMethods.VCP_BRIGHTNESS, ddcValue))
                return true;

            if (attempt < retryCount - 1)
                Thread.Sleep(RetryDelayMilliseconds);
        }

        return false;
    }

    private static bool TrySetHighLevelBrightness(DdcMonitor monitor, int brightnessPercent)
    {
        var range = Math.Max(1, monitor.MaxDdcBrightness - monitor.MinNativeBrightness);
        var nativeBrightness = (uint)Math.Round(monitor.MinNativeBrightness + (brightnessPercent / 100.0 * range));
        return NativeMethods.SetMonitorBrightness(monitor.Handle, nativeBrightness);
    }

    private static bool TryGetVcpBrightness(DdcMonitor monitor, out int brightnessPercent, int retryCount)
    {
        brightnessPercent = 0;
        for (var attempt = 0; attempt < retryCount; attempt++)
        {
            if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                    monitor.Handle,
                    NativeMethods.VCP_BRIGHTNESS,
                    out _,
                    out var current,
                    out var max) && max > 0)
            {
                brightnessPercent = (int)Math.Round(current * 100.0 / max);
                return true;
            }

            if (attempt < retryCount - 1)
                Thread.Sleep(RetryDelayMilliseconds);
        }

        return false;
    }

    private static bool TryGetHighLevelBrightness(DdcMonitor monitor, out int brightnessPercent)
    {
        brightnessPercent = 0;
        if (!NativeMethods.GetMonitorBrightness(
                monitor.Handle,
                out var min,
                out var current,
                out var max) || max <= min)
        {
            return false;
        }

        brightnessPercent = (int)Math.Round((current - min) * 100.0 / (max - min));
        return true;
    }

    private static bool TrySetInternalWmiBrightness(int brightnessPercent)
    {
        var watcher = new BrightSync.Core.Brightness.InternalBrightnessWatcher();
        return watcher.TrySetBrightness(brightnessPercent);
    }

    private static bool TryGetInternalWmiBrightness(out int brightnessPercent)
    {
        var watcher = new BrightSync.Core.Brightness.InternalBrightnessWatcher();
        brightnessPercent = watcher.ReadCurrentBrightness();
        return brightnessPercent >= 0;
    }

    private static string GetDeviceName(IntPtr hMonitor, out int resW, out int resH)
    {
        resW = 0;
        resH = 0;
        var mi = new NativeMethods.MONITORINFOEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
        };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            return $"Monitor_0x{hMonitor:X}";

        resW = mi.rcMonitor.Right - mi.rcMonitor.Left;
        resH = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        return mi.szDevice;
    }

    private static int GetRefreshRate(string deviceName)
    {
        var mode = new NativeMethods.DEVMODE
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>()
        };

        return NativeMethods.EnumDisplaySettings(deviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref mode)
            ? (int)Math.Round(Convert.ToDecimal(mode.dmDisplayFrequency))
            : 0;
    }

    private void DisposeGroups()
    {
        foreach (var g in _groups)
            g.Dispose();
        _groups.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Debug("Disposing DDC/CI service");
        lock (_lock)
            DisposeGroups();
    }
}