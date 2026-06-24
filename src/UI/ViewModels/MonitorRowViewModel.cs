using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Interop;
using BrightSync.Core.Monitors;
using Serilog;

namespace BrightSync.UI.ViewModels;

public sealed class MonitorRowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly DdcMonitor _monitor;
    private readonly MonitorProfile _profile;
    private readonly BrightSyncEngine _engine;
    private readonly string _displayName;
    private readonly Action? _onReset;
    private readonly Action<bool>? _onSettingsChanged;
    private readonly Action<MonitorRowViewModel>? _onExpanded;

    private Timer? _contrastDebounce;
    private Timer? _volumeDebounce;
    private Timer? _redGainDebounce;
    private Timer? _greenGainDebounce;
    private Timer? _blueGainDebounce;

    private int? _tempContrast;
    private int? _tempVolume;
    private int? _tempRedGain;
    private int? _tempGreenGain;
    private int? _tempBlueGain;

    private void DebounceVcpWrite(ref Timer? timer, byte vcpCode, int value)
    {
        timer?.Dispose();
        timer = new Timer(_ =>
        {
            Log.Debug("Debounced VCP write. Monitor={Monitor}, VcpCode=0x{Vcp:X2}, Value={Val}", DisplayName, vcpCode, value);
            _engine.Ddc.SetVcpFeature(_monitor, vcpCode, (uint)value);
        }, null, 150, Timeout.Infinite);
    }

    public string DeviceName { get; }
    public string BrandName { get; }
    public string ModelName { get; }

    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;

            _isExpanded = value;
            OnChanged();
            OnChanged(nameof(ChevronIcon));

            if (_isExpanded)
                _onExpanded?.Invoke(this);
        }
    }

    public string ChevronIcon => _isExpanded ? "\uE70D" : "\uE76C";

    private bool _enabled;

    public bool Enabled
    {
        get => UsesWindowsBrightnessControl || _enabled;
        set
        {
            if (UsesWindowsBrightnessControl)
            {
                _enabled = true;
                OnChanged();
                OnChanged(nameof(TargetText));
                return;
            }

            if (!SupportsDdcCi)
            {
                _enabled = false;
                OnChanged();
                OnChanged(nameof(TargetText));
                return;
            }

            if (_enabled == value)
                return;

            _enabled = value;
            _profile.Enabled = value;
            OnChanged();
            OnChanged(nameof(TargetText));
            _onSettingsChanged?.Invoke(false);
        }
    }

    public bool CanToggleEnabled => SupportsDdcCi && !UsesWindowsBrightnessControl;
    public bool CanExpand => true;
    public bool UsesWindowsBrightnessControl => false;
    public bool SupportsBrightnessControl => SupportsDdcCi || UsesWindowsBrightnessControl;
    public bool ShowsPerMonitorAdjustmentControls => SupportsDdcCi && !UsesWindowsBrightnessControl;
    public bool ShowsResetButton => ShowsPerMonitorAdjustmentControls;

    private int _min;

    public int MinBrightness
    {
        get => _min;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (value > _max) value = _max;
            if (_min == value)
                return;

            _min = value;
            _profile.MinBrightness = value;
            OnChanged();
            OnChanged(nameof(TargetText));
            _onSettingsChanged?.Invoke(true);
        }
    }

    private int _max;

    public int MaxBrightness
    {
        get => _max;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (value < _min) value = _min;
            if (_max == value)
                return;

            _max = value;
            _profile.MaxBrightness = value;
            OnChanged();
            OnChanged(nameof(TargetText));
            _onSettingsChanged?.Invoke(true);
        }
    }

    private double _multiplier;

    public double Multiplier
    {
        get => _multiplier;
        set
        {
            var multiplier = Math.Clamp(Math.Round(value, 2), 0.1, 3.0);
            if (Math.Abs(_multiplier - multiplier) < 0.001)
                return;

            _multiplier = multiplier;
            _profile.Multiplier = _multiplier;
            OnChanged();
            OnChanged(nameof(MultiplierDisplay));
            OnChanged(nameof(TargetText));
            _onSettingsChanged?.Invoke(true);
        }
    }

    public string MultiplierDisplay => $"{_multiplier:F2}\u00d7";

    public bool SupportsDdcCi { get; }

    /// <summary>Raw DDC firmware string — shown as subtitle.</summary>
    public string HardwareDescription { get; }

    /// <summary>Best available detected monitor name for display only, never user-saved.</summary>
    public string DisplayName => _displayName;

    public string ResolutionText { get; }
    public string ConnectionText { get; }
    public string BrightnessBackendText { get; }
    public string DetectionBackendText { get; }
    public string DetectionDetailsText { get; }

    public string DdcStatusText => UsesWindowsBrightnessControl
        ? "Windows brightness"
        : SupportsDdcCi
            ? BrightnessBackendText
            : "No brightness control";

    public bool IsInternal { get; }
    public bool IsHdrSupported { get; }
    public bool IsHdrEnabled { get; }
    public ICommand ResetCommand { get; }
    public bool HasCapabilityNotice => UsesWindowsBrightnessControl || !SupportsDdcCi;

    public string CapabilityNoticeText => UsesWindowsBrightnessControl
        ? "Built-in panel brightness is controlled through Windows."
        : "Brightness control is unavailable on this connection.";

    public string DetectionSummaryText => string.IsNullOrWhiteSpace(DetectionBackendText)
        ? "Detection diagnostics unavailable."
        : $"Detection: {DetectionBackendText}";

    /// <summary>Compact info line combining resolution, DDC status, and connection type.</summary>
    public string InfoLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(ResolutionText)) parts.Add(ResolutionText);
            parts.Add(DdcStatusText);
            if (!string.IsNullOrEmpty(ConnectionText)) parts.Add(ConnectionText);
            if (IsHdrEnabled)
                parts.Add("HDR on");
            else if (IsHdrSupported)
                parts.Add("HDR");
            return string.Join(" \u00b7 ", parts);
        }
    }

    public string TargetText
    {
        get
        {
            if (UsesWindowsBrightnessControl)
            {
                var b = _engine.MasterBrightness;
                return b >= 0
                    ? $"Controlled by Windows slider ({b}%)"
                    : "Controlled by Windows slider";
            }

            if (!SupportsDdcCi) return "No brightness control";
            if (!Enabled) return "Disabled";
            var currentBrightness = _engine.MasterBrightness;
            if (currentBrightness < 0) return "\u2014";
            var t = _engine.CalculateTarget(DeviceName, _profile);
            return $"Target: {t}%  (master {currentBrightness}%)";
        }
    }

    public MonitorRowViewModel(
        DdcMonitor monitor,
        MonitorProfile profile,
        BrightSyncEngine engine,
        Action? onReset = null,
        Action<bool>? onSettingsChanged = null,
        Action<MonitorRowViewModel>? onExpanded = null)
    {
        DeviceName = monitor.DeviceName;
        BrandName = monitor.ManufacturerName;
        ModelName = monitor.ModelName;
        _displayName = BuildDisplayName(monitor);
        HardwareDescription = BuildHardwareDescription(monitor, _displayName);
        ResolutionText = BuildResolutionText(monitor);
        ConnectionText = monitor.ConnectionType;
        BrightnessBackendText = monitor.BrightnessBackend;
        DetectionBackendText = monitor.DetectionBackend;
        DetectionDetailsText = monitor.DetectionDetails;
        IsInternal = monitor.IsInternal;
        IsHdrSupported = monitor.IsHdrSupported;
        IsHdrEnabled = monitor.IsHdrEnabled;
        SupportsDdcCi = monitor.SupportsDdcCi;
        _monitor = monitor;
        _profile = profile;
        _engine = engine;
        _onReset = onReset;
        _onSettingsChanged = onSettingsChanged;
        _onExpanded = onExpanded;
        _enabled = UsesWindowsBrightnessControl || (SupportsDdcCi && profile.Enabled);
        _min = profile.MinBrightness;
        _max = profile.MaxBrightness;
        _multiplier = profile.Multiplier;
        ResetCommand = new RelayCommand(Reset);
    }

    public void RefreshTargetText() => OnChanged(nameof(TargetText));

    public void Reset()
    {
        _profile.Reset();
        _tempContrast = null;
        _tempVolume = null;
        _tempRedGain = null;
        _tempGreenGain = null;
        _tempBlueGain = null;
 
        _contrastDebounce?.Dispose(); _contrastDebounce = null;
        _volumeDebounce?.Dispose(); _volumeDebounce = null;
        _redGainDebounce?.Dispose(); _redGainDebounce = null;
        _greenGainDebounce?.Dispose(); _greenGainDebounce = null;
        _blueGainDebounce?.Dispose(); _blueGainDebounce = null;
 
        _enabled = UsesWindowsBrightnessControl || (SupportsDdcCi && _profile.Enabled);
        _min = _profile.MinBrightness;
        _max = _profile.MaxBrightness;
        _multiplier = _profile.Multiplier;
        _isExpanded = false;
        OnChanged(nameof(IsExpanded));
        OnChanged(nameof(Enabled));
        OnChanged(nameof(MinBrightness));
        OnChanged(nameof(MaxBrightness));
        OnChanged(nameof(Multiplier));
        OnChanged(nameof(MultiplierDisplay));
        OnChanged(nameof(TargetText));
        OnChanged(nameof(Contrast));
        OnChanged(nameof(Volume));
        OnChanged(nameof(RedGain));
        OnChanged(nameof(GreenGain));
        OnChanged(nameof(BlueGain));
        OnChanged(nameof(SelectedColorPreset));
        OnChanged(nameof(SelectedInputSource));
        _onReset?.Invoke();
    }

    public record ColorPresetItem(string Name, int Value);
    public record InputSourceItem(string Name, int Value);
 
    private static readonly List<ColorPresetItem> MasterColorPresets = new()
    {
        new ColorPresetItem("sRGB", 1),
        new ColorPresetItem("Display Native", 2),
        new ColorPresetItem("5000K (Warm)", 4),
        new ColorPresetItem("6500K (Normal)", 5),
        new ColorPresetItem("9300K (Cool)", 8),
        new ColorPresetItem("User Defined", 11)
    };
 
    private static readonly List<InputSourceItem> MasterInputSources = new()
    {
        new InputSourceItem("VGA / D-Sub", 1),
        new InputSourceItem("DVI 1", 3),
        new InputSourceItem("DVI 2", 4),
        new InputSourceItem("Composite", 5),
        new InputSourceItem("S-Video", 6),
        new InputSourceItem("Component", 8),
        new InputSourceItem("DisplayPort 1", 15),
        new InputSourceItem("DisplayPort 2", 16),
        new InputSourceItem("HDMI 1", 17),
        new InputSourceItem("HDMI 2", 18),
        new InputSourceItem("USB-C", 27)
    };
 
    public List<ColorPresetItem> ColorPresetsList
    {
        get
        {
            if (_monitor.SupportedPresets != null && _monitor.SupportedPresets.Count > 0)
            {
                var list = MasterColorPresets.Where(p => _monitor.SupportedPresets.Contains((uint)p.Value)).ToList();
                var currentVal = ColorPreset;
                if (!list.Any(p => p.Value == currentVal))
                {
                    list.Add(new ColorPresetItem($"Preset (0x{currentVal:X})", currentVal));
                }
                return list;
            }
            return MasterColorPresets;
        }
    }
 
    public List<InputSourceItem> InputSourcesList
    {
        get
        {
            if (_monitor.SupportedInputs != null && _monitor.SupportedInputs.Count > 0)
            {
                var list = MasterInputSources.Where(i => _monitor.SupportedInputs.Contains((uint)i.Value)).ToList();
                var currentVal = InputSource;
                if (!list.Any(i => i.Value == currentVal))
                {
                    list.Add(new InputSourceItem($"Input (0x{currentVal:X})", currentVal));
                }
                return list;
            }
            return MasterInputSources;
        }
    }
 
    public bool ShowsAdvancedSettings => SupportsContrast || SupportsVolume || SupportsRgbGains || SupportsColorPreset || SupportsInputSource;
 
    public bool SupportsContrast => _monitor.SupportsContrast;
    public int MaxContrast => _monitor.MaxContrast;
 
    public int Contrast
    {
        get => _profile.Contrast ?? _tempContrast ?? _monitor.CurrentContrast;
        set
        {
            var val = Math.Clamp(value, 0, _monitor.MaxContrast);
            if (Contrast == val) return;
            _tempContrast = val;
            _profile.Contrast = val;
            OnChanged();
            DebounceVcpWrite(ref _contrastDebounce, NativeMethods.VCP_CONTRAST, val);
            _onSettingsChanged?.Invoke(true);
        }
    }
 
    public bool SupportsVolume => _monitor.SupportsVolume;
    public int MaxVolume => _monitor.MaxVolume;
 
    public int Volume
    {
        get => _profile.Volume ?? _tempVolume ?? _monitor.CurrentVolume;
        set
        {
            var val = Math.Clamp(value, 0, _monitor.MaxVolume);
            if (Volume == val) return;
            _tempVolume = val;
            _profile.Volume = val;
            OnChanged();
            DebounceVcpWrite(ref _volumeDebounce, NativeMethods.VCP_VOLUME, val);
            _onSettingsChanged?.Invoke(true);
        }
    }
 
    public bool SupportsRgbGains => _monitor.SupportsRgbGains;
    public int MaxRgbGain => _monitor.MaxRgbGain;
 
    public int RedGain
    {
        get => _profile.RedGain ?? _tempRedGain ?? _monitor.CurrentRedGain;
        set
        {
            var val = Math.Clamp(value, 0, _monitor.MaxRgbGain);
            if (RedGain == val) return;
            _tempRedGain = val;
            _profile.RedGain = val;
            OnChanged();
            DebounceVcpWrite(ref _redGainDebounce, NativeMethods.VCP_RED_GAIN, val);
            _onSettingsChanged?.Invoke(true);
        }
    }
 
    public int GreenGain
    {
        get => _profile.GreenGain ?? _tempGreenGain ?? _monitor.CurrentGreenGain;
        set
        {
            var val = Math.Clamp(value, 0, _monitor.MaxRgbGain);
            if (GreenGain == val) return;
            _tempGreenGain = val;
            _profile.GreenGain = val;
            OnChanged();
            DebounceVcpWrite(ref _greenGainDebounce, NativeMethods.VCP_GREEN_GAIN, val);
            _onSettingsChanged?.Invoke(true);
        }
    }
 
    public int BlueGain
    {
        get => _profile.BlueGain ?? _tempBlueGain ?? _monitor.CurrentBlueGain;
        set
        {
            var val = Math.Clamp(value, 0, _monitor.MaxRgbGain);
            if (BlueGain == val) return;
            _tempBlueGain = val;
            _profile.BlueGain = val;
            OnChanged();
            DebounceVcpWrite(ref _blueGainDebounce, NativeMethods.VCP_BLUE_GAIN, val);
            _onSettingsChanged?.Invoke(true);
        }
    }
 
    public bool SupportsColorPreset => _monitor.SupportsColorPreset;
 
    public int ColorPreset
    {
        get => _profile.ColorPreset ?? _monitor.CurrentColorPreset;
        set
        {
            if (ColorPreset == value) return;
            _profile.ColorPreset = value;
            _engine.Ddc.SetVcpFeature(_monitor, NativeMethods.VCP_COLOR_PRESET, (uint)value);
            OnChanged();
            _onSettingsChanged?.Invoke(false);
        }
    }
 
    public ColorPresetItem? SelectedColorPreset
    {
        get => ColorPresetsList.FirstOrDefault(p => p.Value == ColorPreset) ?? new ColorPresetItem($"Unknown (0x{ColorPreset:X})", ColorPreset);
        set
        {
            if (value != null)
            {
                ColorPreset = value.Value;
                OnChanged();
            }
        }
    }
 
    public bool SupportsInputSource => _monitor.SupportsInputSource;
 
    public int InputSource
    {
        get => _profile.InputSource ?? _monitor.CurrentInputSource;
        set
        {
            if (InputSource == value) return;
            _profile.InputSource = value;
            _engine.Ddc.SetVcpFeature(_monitor, NativeMethods.VCP_INPUT_SOURCE, (uint)value);
            OnChanged();
            _onSettingsChanged?.Invoke(false);
        }
    }
 
    public InputSourceItem? SelectedInputSource
    {
        get => InputSourcesList.FirstOrDefault(i => i.Value == InputSource) ?? new InputSourceItem($"Unknown (0x{InputSource:X})", InputSource);
        set
        {
            if (value != null)
            {
                InputSource = value.Value;
                OnChanged();
            }
        }
    }

    private static string BuildDisplayName(DdcMonitor monitor)
    {
        if (!string.IsNullOrWhiteSpace(monitor.ManufacturerName) &&
            !string.IsNullOrWhiteSpace(monitor.ModelName))
        {
            return $"{monitor.ManufacturerName} {monitor.ModelName}";
        }

        if (!string.IsNullOrWhiteSpace(monitor.FriendlyName))
            return monitor.FriendlyName;

        return monitor.Description;
    }

    private static string BuildResolutionText(DdcMonitor monitor)
    {
        if (monitor.ResolutionWidth <= 0 || monitor.ResolutionHeight <= 0)
            return string.Empty;

        var resolution = $"{monitor.ResolutionWidth}x{monitor.ResolutionHeight}";
        return monitor.RefreshRateHz > 0
            ? $"{resolution}@{monitor.RefreshRateHz}"
            : resolution;
    }

    private static string BuildHardwareDescription(DdcMonitor monitor, string displayName)
    {
        var description = monitor.Description?.Trim() ?? string.Empty;
        return string.Equals(description, displayName, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : description;
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}