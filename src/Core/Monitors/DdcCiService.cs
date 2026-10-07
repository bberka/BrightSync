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
    private readonly object _refreshLock = new();
    private readonly IDdcMonitorProvider? _monitorProvider;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private List<DdcMonitor> _monitors = new();
    private List<PhysicalMonitorGroup> _groups = new();
    private MonitorDisplaySnapshot[] _monitorDisplaySnapshots = [];
    private TaskCompletionSource? _refreshCompletion;
    private int _disposed;
    private int _disposing;
    private bool _refreshInProgress;
    private bool _refreshRequested;

    public DdcCiService(ConfigManager config)
        : this(config, monitorProvider: null)
    {
    }

    internal DdcCiService(ConfigManager config, IDdcMonitorProvider? monitorProvider)
    {
        _config = config;
        _monitorProvider = monitorProvider;
        Refresh();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    private bool IsStopping => Volatile.Read(ref _disposing) != 0;

    /// <summary>Re-enumerates all connected monitors. Call after display topology changes.</summary>
    public void Refresh()
    {
        TaskCompletionSource completion;
        var execute = false;

        lock (_refreshLock)
        {
            if (IsDisposed || IsStopping)
                return;

            _refreshRequested = true;
            if (_refreshInProgress)
            {
                completion = _refreshCompletion!;
            }
            else
            {
                _refreshInProgress = true;
                _refreshRequested = false;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _refreshCompletion = completion;
                execute = true;
            }
        }

        if (execute)
        {
            ExecuteRefreshLoop(completion);
            return;
        }

        // A concurrent caller waits for the active pass and any coalesced
        // follow-up pass, preserving the synchronous behavior of Refresh().
        completion.Task.GetAwaiter().GetResult();
    }

    /// <summary>Returns a snapshot of currently known DDC/CI monitors.</summary>
    public IReadOnlyList<DdcMonitor> GetMonitors()
    {
        lock (_lock)
            return IsDisposed || IsStopping ? [] : _monitors.ToList();
    }

    /// <summary>
    /// Returns display metadata without waiting for monitor hardware operations.
    /// The snapshot is replaced atomically after a monitor refresh completes.
    /// </summary>
    public IReadOnlyList<MonitorDisplaySnapshot> GetMonitorDisplaySnapshot()
        => IsStopping ? [] : Volatile.Read(ref _monitorDisplaySnapshots);

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
            if (!IsCurrentMonitorLocked(monitor)) return false;
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
            if (!IsCurrentMonitorLocked(monitor)) return false;
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

    private DdcMonitorSet EnumerateMonitors(bool useLegacyDetection, CancellationToken cancellationToken)
    {
        var hMonitors = new List<IntPtr>();
        var monitors = new List<DdcMonitor>();
        var groups = new List<PhysicalMonitorGroup>();

        try
        {
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
                cancellationToken.ThrowIfCancellationRequested();
                var deviceName = GetDeviceName(hMonitor, out var resW, out var resH);

                var internalDetection = MonitorDetectionResolver.Resolve(deviceName, deviceName, useLegacyDetection);
                if (internalDetection.IsInternal)
                {
                    var internalHdrInfo = internalDetection.HdrInfo;
                    var tempWatcher = new BrightSync.Core.Brightness.InternalBrightnessWatcher();
                    var currentBrightness = tempWatcher.ReadCurrentBrightness();

                    monitors.Add(new DdcMonitor
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
                groups.Add(group);
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
                    cancellationToken.ThrowIfCancellationRequested();
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

                    ProbeAdvancedCapabilities(m, cancellationToken);
                    monitors.Add(m);

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

            return new DdcMonitorSet(monitors, groups);
        }
        catch
        {
            DisposeGroups(groups);
            throw;
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
            return IsCurrentMonitorLocked(monitor) &&
                   !monitor.IsInternal &&
                   TrySetVcpFeatureCore(monitor, vcpCode, value, CancellationToken.None);
        }
    }

    public bool GetVcpFeature(DdcMonitor monitor, byte vcpCode, out uint currentValue, out uint maxValue)
    {
        currentValue = 0;
        maxValue = 0;
        lock (_lock)
        {
            return IsCurrentMonitorLocked(monitor) &&
                   !monitor.IsInternal &&
                   TryGetVcpFeatureCore(
                       monitor,
                       vcpCode,
                       out currentValue,
                       out maxValue,
                       CancellationToken.None);
        }
    }

    private static bool TrySetVcpFeatureCore(
        DdcMonitor monitor,
        byte vcpCode,
        uint value,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.SetVCPFeature(monitor.Handle, vcpCode, value))
                return true;
            if (attempt < 1)
                Thread.Sleep(RetryDelayMilliseconds);
        }

        return false;
    }

    private static bool TryGetVcpFeatureCore(
        DdcMonitor monitor,
        byte vcpCode,
        out uint currentValue,
        out uint maxValue,
        CancellationToken cancellationToken)
    {
        currentValue = 0;
        maxValue = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private void ProbeAdvancedCapabilities(DdcMonitor monitor, CancellationToken cancellationToken)
    {
        if (monitor.IsInternal)
            return;

        Log.Debug("Probing advanced capabilities for monitor: {Monitor}", monitor.FriendlyName);

        var capStr = GetCapabilitiesString(monitor.Handle);
        cancellationToken.ThrowIfCancellationRequested();
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

        if (TryGetVcpFeatureCore(monitor, NativeMethods.VCP_CONTRAST, out var contrastVal, out var contrastMax,
                cancellationToken))
        {
            monitor.SupportsContrast = true;
            monitor.CurrentContrast = (int)contrastVal;
            monitor.MaxContrast = (int)contrastMax;
        }

        if (TryGetVcpFeatureCore(monitor, NativeMethods.VCP_VOLUME, out var volVal, out var volMax,
                cancellationToken))
        {
            monitor.SupportsVolume = true;
            monitor.CurrentVolume = (int)volVal;
            monitor.MaxVolume = (int)volMax;
        }

        if (TryGetVcpFeatureCore(monitor, NativeMethods.VCP_COLOR_PRESET, out var presetVal, out _,
                cancellationToken))
        {
            monitor.SupportsColorPreset = true;
            monitor.CurrentColorPreset = (int)presetVal;
        }

        if (TryGetVcpFeatureCore(monitor, NativeMethods.VCP_RED_GAIN, out var redVal, out var rgbMax,
                cancellationToken) &&
            TryGetVcpFeatureCore(monitor, NativeMethods.VCP_GREEN_GAIN, out var greenVal, out _,
                cancellationToken) &&
            TryGetVcpFeatureCore(monitor, NativeMethods.VCP_BLUE_GAIN, out var blueVal, out _,
                cancellationToken))
        {
            monitor.SupportsRgbGains = true;
            monitor.CurrentRedGain = (int)redVal;
            monitor.CurrentGreenGain = (int)greenVal;
            monitor.CurrentBlueGain = (int)blueVal;
            monitor.MaxRgbGain = (int)rgbMax;
        }

        if (TryGetVcpFeatureCore(monitor, NativeMethods.VCP_INPUT_SOURCE, out var inputVal, out _,
                cancellationToken))
        {
            monitor.SupportsInputSource = true;
            monitor.CurrentInputSource = (int)inputVal;
        }

        monitor.RawCapabilitiesString = capStr ?? string.Empty;

        if (TryGetVcpFeatureCore(monitor, NativeMethods.VCP_SHARPNESS, out var sharpnessVal, out var sharpnessMax,
                cancellationToken))
        {
            monitor.SupportsSharpness = true;
            monitor.CurrentSharpness = (int)sharpnessVal;
            monitor.MaxSharpness = (int)sharpnessMax;
        }

        if (TryGetVcpFeatureCore(monitor, NativeMethods.VCP_SATURATION, out var saturationVal, out var saturationMax,
                cancellationToken))
        {
            monitor.SupportsSaturation = true;
            monitor.CurrentSaturation = (int)saturationVal;
            monitor.MaxSaturation = (int)saturationMax;
        }

        if (TryGetVcpFeatureCore(monitor, NativeMethods.VCP_GAMMA, out var gammaVal, out _,
                cancellationToken))
        {
            monitor.SupportsGamma = true;
            monitor.CurrentGamma = (int)gammaVal;
        }

        if (TryGetVcpFeatureCore(monitor, NativeMethods.VCP_POWER_CONTROL, out var powerVal, out _,
                cancellationToken))
        {
            monitor.SupportsPowerControl = true;
            monitor.CurrentPowerState = (int)powerVal;
        }
    }

    private void ExecuteRefreshLoop(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                try
                {
                    RefreshCore(_lifetimeCts.Token);
                }
                catch (OperationCanceledException) when (IsDisposed || IsStopping)
                {
                    Log.Debug("DDC/CI refresh canceled during service disposal");
                }
                catch (Exception ex)
                {
                    // Refresh can run from timer/event threads. Keep failures
                    // observed and preserve the last known-good monitor set.
                    Log.Error(ex, "DDC/CI monitor refresh failed");
                }

                lock (_refreshLock)
                {
                    if (IsDisposed || !_refreshRequested)
                    {
                        _refreshRequested = false;
                        _refreshInProgress = false;
                        if (ReferenceEquals(_refreshCompletion, completion))
                            _refreshCompletion = null;
                        completion.TrySetResult();
                        return;
                    }

                    // Consume the request and make exactly one follow-up pass.
                    _refreshRequested = false;
                }
            }
        }
        catch (Exception ex)
        {
            // Keep the coordinator itself exception-contained even if a future
            // change fails outside the per-pass refresh boundary.
            Log.Error(ex, "Unexpected DDC/CI refresh coordinator failure");
            lock (_refreshLock)
            {
                _refreshRequested = false;
                _refreshInProgress = false;
                if (ReferenceEquals(_refreshCompletion, completion))
                    _refreshCompletion = null;
            }

            completion.TrySetResult();
        }
    }

    private void RefreshCore(CancellationToken cancellationToken)
    {
        if (IsDisposed || IsStopping || cancellationToken.IsCancellationRequested)
            return;

        MonitorNameResolver.InvalidateCache();
        cancellationToken.ThrowIfCancellationRequested();

        DdcMonitorSet? candidate = null;
        try
        {
            var useLegacyDetection = _config.Config.UseLegacyDdcCiDetection;
            var refreshedMonitors = _monitorProvider?.Enumerate(
                                        useLegacyDetection,
                                        cancellationToken) ??
                                    EnumerateMonitors(useLegacyDetection, cancellationToken);
            candidate = refreshedMonitors;
            cancellationToken.ThrowIfCancellationRequested();

            lock (_lock)
            {
                if (IsDisposed || IsStopping || cancellationToken.IsCancellationRequested)
                    return;

                var newMonitors = refreshedMonitors.Monitors.ToList();
                var newGroups = refreshedMonitors.Groups.ToList();

                var oldGroups = _groups;
                _groups = newGroups;
                _monitors = newMonitors;
                // Ownership transfers to _groups once the candidate is published.
                candidate = null;

                // Publish only immutable display metadata. The quick UI can read
                // this while hardware enumeration for a later pass is in flight.
                Volatile.Write(ref _monitorDisplaySnapshots, newMonitors
                    .Select(m => new MonitorDisplaySnapshot(
                        m.DeviceName,
                        m.ManufacturerName,
                        m.ModelName,
                        m.FriendlyName,
                        m.Description,
                        m.SupportsDdcCi))
                    .ToArray());

                DisposeGroups(oldGroups);

                Log.Information(
                    "DDC/CI refresh finished. DetectionMode={DetectionMode}, TotalMonitors={TotalMonitors}, ControllableMonitors={ControllableMonitors}",
                    useLegacyDetection ? "Legacy" : "Modern",
                    newMonitors.Count,
                    newMonitors.Count(m => m.SupportsDdcCi));
            }

        }
        finally
        {
            candidate?.DisposeGroups();
        }
    }

    private bool IsCurrentMonitorLocked(DdcMonitor monitor)
    {
        if (monitor is null || IsDisposed || IsStopping)
            return false;

        // GetMonitors intentionally returns shallow monitor objects for existing
        // callers. Reference membership makes every object from a replaced
        // snapshot fail before its native handle can be used.
        return _monitors.Any(current => ReferenceEquals(current, monitor));
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
        DisposeGroups(_groups);
        _groups.Clear();
    }

    public void Dispose()
    {
        lock (_refreshLock)
        {
            if (IsDisposed || Interlocked.Exchange(ref _disposing, 1) != 0)
                return;

            _refreshRequested = false;
            _lifetimeCts.Cancel();
        }

        Log.Debug("Disposing DDC/CI service");
        lock (_lock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            DisposeGroups();
            _monitors = new List<DdcMonitor>();
            Volatile.Write(ref _monitorDisplaySnapshots, []);
        }
    }

    private static void DisposeGroups(IEnumerable<PhysicalMonitorGroup> groups)
    {
        foreach (var group in groups)
            group.Dispose();
    }
}

/// <summary>Immutable monitor metadata used by UI surfaces that do not need native handles.</summary>
public sealed record MonitorDisplaySnapshot(
    string DeviceName,
    string ManufacturerName,
    string ModelName,
    string FriendlyName,
    string Description,
    bool SupportsDdcCi);

internal interface IDdcMonitorProvider
{
    DdcMonitorSet Enumerate(bool useLegacyDetection, CancellationToken cancellationToken);
}

internal sealed class DdcMonitorSet
{
    public IReadOnlyList<DdcMonitor> Monitors { get; }
    public IReadOnlyList<PhysicalMonitorGroup> Groups { get; }

    internal DdcMonitorSet(IEnumerable<DdcMonitor> monitors)
        : this(monitors, [])
    {
    }

    internal DdcMonitorSet(
        IEnumerable<DdcMonitor> monitors,
        IEnumerable<PhysicalMonitorGroup> groups)
    {
        Monitors = monitors.ToList();
        Groups = groups.ToList();
    }

    public void DisposeGroups()
    {
        foreach (var group in Groups)
            group.Dispose();
    }
}
