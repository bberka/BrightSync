using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;
using BrightSync.Core.Brightness;
using BrightSync.Core.Colors;
using BrightSync.Core.Config;
using BrightSync.Core.Interop;
using BrightSync.Core.Monitors;
using BrightSync.UI;
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
    private readonly List<int> _supportedRefreshRates;
    private readonly List<string> _installedColorProfiles;

    private Timer? _contrastDebounce;
    private Timer? _volumeDebounce;
    private Timer? _redGainDebounce;
    private Timer? _greenGainDebounce;
    private Timer? _blueGainDebounce;
    private Timer? _sharpnessDebounce;
    private Timer? _saturationDebounce;

    private int? _tempContrast;
    private int? _tempVolume;
    private int? _tempRedGain;
    private int? _tempGreenGain;
    private int? _tempBlueGain;
    private int? _tempSharpness;
    private int? _tempSaturation;

    // Custom VCP Console state
    private string _customVcpCodeHex = "E2";
    private string _customVcpValue = "0";
    private string _customVcpLastResult = string.Empty;
    private string _customVcpActionName = string.Empty;
    private readonly System.Collections.ObjectModel.ObservableCollection<CustomVcpActionViewModel> _customActions = new();

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

        _supportedRefreshRates = DisplaySettingsService.GetSupportedRefreshRates(DeviceName);
        if (_supportedRefreshRates.Count == 0 && monitor.RefreshRateHz > 0)
        {
            _supportedRefreshRates.Add(monitor.RefreshRateHz);
        }
        _installedColorProfiles = ColorProfileManager.GetInstalledColorProfiles();

        ReloadCustomActions();

        ResetCommand = new RelayCommand(Reset);
        QueryCustomVcpCommand = new RelayCommand(QueryCustomVcp);
        WriteCustomVcpCommand = new RelayCommand(WriteCustomVcp);
        SaveCustomActionCommand = new RelayCommand(SaveCustomAction);
        OpenHdrSettingsCommand = new RelayCommand(OpenHdrSettings);
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
        _tempSharpness = null;
        _tempSaturation = null;
 
        _contrastDebounce?.Dispose(); _contrastDebounce = null;
        _volumeDebounce?.Dispose(); _volumeDebounce = null;
        _redGainDebounce?.Dispose(); _redGainDebounce = null;
        _greenGainDebounce?.Dispose(); _greenGainDebounce = null;
        _blueGainDebounce?.Dispose(); _blueGainDebounce = null;
        _sharpnessDebounce?.Dispose(); _sharpnessDebounce = null;
        _saturationDebounce?.Dispose(); _saturationDebounce = null;
 
        _enabled = UsesWindowsBrightnessControl || (SupportsDdcCi && _profile.Enabled);
        _min = _profile.MinBrightness;
        _max = _profile.MaxBrightness;
        _multiplier = _profile.Multiplier;
        _isExpanded = false;

        ReloadCustomActions();

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

        OnChanged(nameof(Sharpness));
        OnChanged(nameof(Saturation));
        OnChanged(nameof(Gamma));
        OnChanged(nameof(PowerState));
        OnChanged(nameof(SelectedPowerState));
        OnChanged(nameof(SelectedRefreshRate));
        OnChanged(nameof(SelectedColorProfile));
        OnChanged(nameof(CustomVcpCodeHex));
        OnChanged(nameof(CustomVcpValue));
        OnChanged(nameof(CustomVcpLastResult));
        OnChanged(nameof(CustomVcpActionName));

        _selectedVcpCodeItem = null;
        OnChanged(nameof(AdvancedFeaturesEnabled));
        OnChanged(nameof(ShowCustomVcpConsole));
        OnChanged(nameof(SelectedVcpCodeItem));
 
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
 
    public bool HasAdvancedFeatures => SupportsDdcCi || SupportedRefreshRates.Count > 1 || InstalledColorProfiles.Count > 0;

    public bool ShowsAdvancedSettings => AdvancedFeaturesEnabled && HasAdvancedFeatures;
 
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
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public ICommand QueryCustomVcpCommand { get; }
    public ICommand WriteCustomVcpCommand { get; }
    public ICommand SaveCustomActionCommand { get; }
    public ICommand OpenHdrSettingsCommand { get; }

    public List<int> SupportedRefreshRates => _supportedRefreshRates;

    public int? SelectedRefreshRate
    {
        get => _profile.RefreshRate ?? (_monitor.RefreshRateHz > 0 ? _monitor.RefreshRateHz : null);
        set
        {
            if (value == null) return;
            if (_profile.RefreshRate == value) return;
            _profile.RefreshRate = value;
            DisplaySettingsService.SetRefreshRate(DeviceName, value.Value);
            OnChanged();
            _onSettingsChanged?.Invoke(false);
        }
    }

    public List<string> InstalledColorProfiles => _installedColorProfiles;

    public string? SelectedColorProfile
    {
        get => _profile.AssociatedColorProfile ?? ColorProfileManager.GetActiveColorProfile(DeviceName);
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (_profile.AssociatedColorProfile == value) return;
            _profile.AssociatedColorProfile = value;
            ColorProfileManager.SetActiveColorProfile(DeviceName, value);
            OnChanged();
            _onSettingsChanged?.Invoke(false);
        }
    }

    public bool SupportsSharpness => _monitor.SupportsSharpness;
    public int MaxSharpness => _monitor.MaxSharpness;

    public int Sharpness
    {
        get => _profile.Sharpness ?? _tempSharpness ?? _monitor.CurrentSharpness;
        set
        {
            var val = Math.Clamp(value, 0, _monitor.MaxSharpness);
            if (Sharpness == val) return;
            _tempSharpness = val;
            _profile.Sharpness = val;
            OnChanged();
            DebounceVcpWrite(ref _sharpnessDebounce, NativeMethods.VCP_SHARPNESS, val);
            _onSettingsChanged?.Invoke(true);
        }
    }

    public bool SupportsSaturation => _monitor.SupportsSaturation;
    public int MaxSaturation => _monitor.MaxSaturation;

    public int Saturation
    {
        get => _profile.Saturation ?? _tempSaturation ?? _monitor.CurrentSaturation;
        set
        {
            var val = Math.Clamp(value, 0, _monitor.MaxSaturation);
            if (Saturation == val) return;
            _tempSaturation = val;
            _profile.Saturation = val;
            OnChanged();
            DebounceVcpWrite(ref _saturationDebounce, NativeMethods.VCP_SATURATION, val);
            _onSettingsChanged?.Invoke(true);
        }
    }

    public bool SupportsGamma => _monitor.SupportsGamma;

    public int Gamma
    {
        get => _profile.Gamma ?? _monitor.CurrentGamma;
        set
        {
            if (Gamma == value) return;
            _profile.Gamma = value;
            _engine.Ddc.SetVcpFeature(_monitor, NativeMethods.VCP_GAMMA, (uint)value);
            OnChanged();
            _onSettingsChanged?.Invoke(false);
        }
    }

    public bool SupportsPowerControl => _monitor.SupportsPowerControl;

    public int PowerState
    {
        get => _profile.PowerState ?? _monitor.CurrentPowerState;
        set
        {
            if (PowerState == value) return;
            _profile.PowerState = value;
            _engine.Ddc.SetVcpFeature(_monitor, NativeMethods.VCP_POWER_CONTROL, (uint)value);
            OnChanged();
            _onSettingsChanged?.Invoke(false);
        }
    }

    public record PowerStateItem(string Name, int Value);
    
    private static readonly List<PowerStateItem> PowerStates = new()
    {
        new PowerStateItem("On", 1),
        new PowerStateItem("Standby", 4),
        new PowerStateItem("Deep Sleep", 5)
    };

    public List<PowerStateItem> PowerStatesList => PowerStates;

    public PowerStateItem? SelectedPowerState
    {
        get => PowerStatesList.FirstOrDefault(p => p.Value == PowerState) ?? new PowerStateItem($"Unknown (0x{PowerState:X})", PowerState);
        set
        {
            if (value != null)
            {
                PowerState = value.Value;
                OnChanged();
            }
        }
    }

    public string CustomVcpCodeHex
    {
        get => _customVcpCodeHex;
        set
        {
            _customVcpCodeHex = value;
            OnChanged();
        }
    }

    public string CustomVcpValue
    {
        get => _customVcpValue;
        set
        {
            _customVcpValue = value;
            OnChanged();
        }
    }

    public string CustomVcpLastResult
    {
        get => _customVcpLastResult;
        set
        {
            _customVcpLastResult = value;
            OnChanged();
        }
    }

    public string CustomVcpActionName
    {
        get => _customVcpActionName;
        set
        {
            _customVcpActionName = value;
            OnChanged();
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<CustomVcpActionViewModel> CustomActions => _customActions;

    public List<string> ParsedCapabilities
    {
        get
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(_monitor.RawCapabilitiesString))
                return list;

            try
            {
                var cap = _monitor.RawCapabilitiesString;
                int vcpIdx = cap.IndexOf("vcp", StringComparison.OrdinalIgnoreCase);
                if (vcpIdx >= 0)
                {
                    int openParen = cap.IndexOf('(', vcpIdx);
                    if (openParen >= 0)
                    {
                        int closeParen = cap.IndexOf(')', openParen);
                        if (closeParen > openParen)
                        {
                            var vcpSection = cap.Substring(openParen + 1, closeParen - openParen - 1);
                            var parts = vcpSection.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var part in parts)
                            {
                                if (byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out byte code))
                                {
                                    list.Add($"0x{code:X2}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to parse monitor capabilities string");
            }
            return list;
        }
    }

    private void QueryCustomVcp()
    {
        if (string.IsNullOrWhiteSpace(CustomVcpCodeHex))
        {
            CustomVcpLastResult = "Error: Enter a VCP code in Hex.";
            return;
        }

        if (!byte.TryParse(CustomVcpCodeHex, System.Globalization.NumberStyles.HexNumber, null, out byte code))
        {
            CustomVcpLastResult = $"Error: Invalid Hex VCP code '{CustomVcpCodeHex}'.";
            return;
        }

        if (_engine.Ddc.GetVcpFeature(_monitor, code, out uint currentValue, out uint maxValue))
        {
            CustomVcpLastResult = $"VCP 0x{code:X2}: Current = {currentValue}, Max = {maxValue}";
            CustomVcpValue = currentValue.ToString();
        }
        else
        {
            CustomVcpLastResult = $"Failed to read VCP 0x{code:X2}.";
        }
    }

    private void WriteCustomVcp()
    {
        if (string.IsNullOrWhiteSpace(CustomVcpCodeHex))
        {
            CustomVcpLastResult = "Error: Enter a VCP code in Hex.";
            return;
        }

        if (!byte.TryParse(CustomVcpCodeHex, System.Globalization.NumberStyles.HexNumber, null, out byte code))
        {
            CustomVcpLastResult = $"Error: Invalid Hex VCP code '{CustomVcpCodeHex}'.";
            return;
        }

        uint value;
        bool parseSuccess = false;
        var valStr = CustomVcpValue?.Trim() ?? string.Empty;
        if (valStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            parseSuccess = uint.TryParse(valStr.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out value);
        }
        else
        {
            parseSuccess = uint.TryParse(valStr, out value);
        }

        if (!parseSuccess)
        {
            CustomVcpLastResult = $"Error: Invalid Value '{CustomVcpValue}'.";
            return;
        }

        if (_engine.Ddc.SetVcpFeature(_monitor, code, value))
        {
            CustomVcpLastResult = $"Successfully wrote {value} (0x{value:X}) to VCP 0x{code:X2}";
        }
        else
        {
            CustomVcpLastResult = $"Failed to write to VCP 0x{code:X2}.";
        }
    }

    private void SaveCustomAction()
    {
        if (string.IsNullOrWhiteSpace(CustomVcpActionName))
        {
            CustomVcpLastResult = "Error: Enter an Action Name.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CustomVcpCodeHex) ||
            !byte.TryParse(CustomVcpCodeHex, System.Globalization.NumberStyles.HexNumber, null, out byte code))
        {
            CustomVcpLastResult = "Error: Enter a valid Hex VCP code.";
            return;
        }

        uint value;
        bool parseSuccess = false;
        var valStr = CustomVcpValue?.Trim() ?? string.Empty;
        if (valStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            parseSuccess = uint.TryParse(valStr.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out value);
        }
        else
        {
            parseSuccess = uint.TryParse(valStr, out value);
        }

        if (!parseSuccess)
        {
            CustomVcpLastResult = "Error: Enter a valid Value.";
            return;
        }

        var existing = _profile.CustomActions.FirstOrDefault(a => a.Name.Equals(CustomVcpActionName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.VcpCode = code;
            existing.Value = value;
        }
        else
        {
            _profile.CustomActions.Add(new CustomVcpActionProfile
            {
                Name = CustomVcpActionName,
                VcpCode = code,
                Value = value
            });
        }

        _onSettingsChanged?.Invoke(true); // Save config

        ReloadCustomActions();
        CustomVcpLastResult = $"Saved shortcut '{CustomVcpActionName}'";
        CustomVcpActionName = string.Empty;
    }

    private void ReloadCustomActions()
    {
        _customActions.Clear();
        foreach (var action in _profile.CustomActions)
        {
            var a = action;
            _customActions.Add(new CustomVcpActionViewModel(
                a.Name,
                a.VcpCode,
                a.Value,
                () => ExecuteVcpAction(a),
                () => DeleteVcpAction(a)
            ));
        }
    }

    private void ExecuteVcpAction(CustomVcpActionProfile action)
    {
        if (_engine.Ddc.SetVcpFeature(_monitor, action.VcpCode, action.Value))
        {
            CustomVcpLastResult = $"Executed: wrote {action.Value} to 0x{action.VcpCode:X2}";
        }
        else
        {
            CustomVcpLastResult = $"Failed to execute: write to 0x{action.VcpCode:X2} failed";
        }
    }

    private void DeleteVcpAction(CustomVcpActionProfile action)
    {
        _profile.CustomActions.Remove(action);
        _onSettingsChanged?.Invoke(true); // Save config
        ReloadCustomActions();
        CustomVcpLastResult = $"Deleted shortcut '{action.Name}'";
    }

    private void OpenHdrSettings()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:display",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open Windows Display settings");
        }
    }

    public bool AdvancedFeaturesEnabled
    {
        get => _profile.AdvancedFeaturesEnabled;
        set
        {
            if (_profile.AdvancedFeaturesEnabled == value) return;
            _profile.AdvancedFeaturesEnabled = value;
            OnChanged();
            OnChanged(nameof(ShowsAdvancedSettings));
            _onSettingsChanged?.Invoke(true);
            if (value)
            {
                _engine.ApplyPersistedSettings(_monitor);
            }
        }
    }

    public bool ShowCustomVcpConsole
    {
        get => _profile.ShowCustomVcpConsole;
        set
        {
            if (_profile.ShowCustomVcpConsole == value) return;
            _profile.ShowCustomVcpConsole = value;
            OnChanged();
            _onSettingsChanged?.Invoke(true);
        }
    }

    public record VcpCodeItem(string Name, string HexCode)
    {
        public override string ToString() => Name;
    }

    private static readonly Dictionary<byte, string> KnownVcpNames = new()
    {
        { 0x02, "New Control Value" },
        { 0x04, "Revert to Factory Defaults" },
        { 0x06, "De-magnetize" },
        { 0x08, "Revert to Color Defaults" },
        { 0x0A, "Revert to Position Defaults" },
        { 0x0C, "Revert to Size Defaults" },
        { 0x10, "Brightness" },
        { 0x12, "Contrast" },
        { 0x14, "Color Preset" },
        { 0x16, "Red Gain" },
        { 0x18, "Green Gain" },
        { 0x1A, "Blue Gain" },
        { 0x52, "Active Control" },
        { 0x60, "Input Source" },
        { 0x62, "Volume" },
        { 0x6C, "Red Black Level" },
        { 0x6E, "Green Black Level" },
        { 0x70, "Blue Black Level" },
        { 0x72, "Gamma" },
        { 0x87, "Sharpness" },
        { 0x8A, "Saturation" },
        { 0xD6, "Power Control" },
        { 0xDF, "VCP Version" },
        { 0xE2, "Samsung Eye Saver Mode" },
        { 0xF2, "Samsung Black Equalizer" }
    };

    public List<VcpCodeItem> SupportedVcpCodesList
    {
        get
        {
            var list = new List<VcpCodeItem>();
            var parsed = ParsedCapabilities;
            
            if (parsed.Count > 0)
            {
                foreach (var hex in parsed)
                {
                    if (byte.TryParse(hex.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out byte code))
                    {
                        if (KnownVcpNames.TryGetValue(code, out var name))
                        {
                            list.Add(new VcpCodeItem($"{name} ({hex})", hex.Replace("0x", "")));
                        }
                        else
                        {
                            list.Add(new VcpCodeItem($"VCP {hex}", hex.Replace("0x", "")));
                        }
                    }
                }
            }
            else
            {
                foreach (var kvp in KnownVcpNames.OrderBy(k => k.Key))
                {
                    list.Add(new VcpCodeItem($"{kvp.Value} (0x{kvp.Key:X2})", $"{kvp.Key:X2}"));
                }
            }
            return list;
        }
    }

    private VcpCodeItem? _selectedVcpCodeItem;
    public VcpCodeItem? SelectedVcpCodeItem
    {
        get => _selectedVcpCodeItem ?? SupportedVcpCodesList.FirstOrDefault(item => item.HexCode.Equals(CustomVcpCodeHex, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value != null)
            {
                _selectedVcpCodeItem = value;
                CustomVcpCodeHex = value.HexCode;
                OnChanged();
            }
        }
    }
}

public sealed class CustomVcpActionViewModel
{
    public string Name { get; }
    public byte VcpCode { get; }
    public uint Value { get; }
    public string DisplayText => $"{Name} (0x{VcpCode:X2} = {Value})";
    public ICommand ExecuteCommand { get; }
    public ICommand DeleteCommand { get; }

    public CustomVcpActionViewModel(string name, byte vcpCode, uint value, Action execute, Action delete)
    {
        Name = name;
        VcpCode = vcpCode;
        Value = value;
        ExecuteCommand = new RelayCommand(execute);
        DeleteCommand = new RelayCommand(delete);
    }
}