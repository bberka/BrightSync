using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;

namespace BrightSync.UI.ViewModels;

public sealed class MonitorRowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly MonitorProfile _profile;
    private readonly BrightSyncEngine _engine;
    private readonly string _displayName;
    private readonly Action? _onReset;
    private readonly Action<bool>? _onSettingsChanged;
    private readonly Action<MonitorRowViewModel>? _onExpanded;

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

            if (_isExpanded)
                _onExpanded?.Invoke(this);
        }
    }

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
    public bool UsesWindowsBrightnessControl => IsInternal;
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
                var b = _engine.LastInternalBrightness;
                return b >= 0
                    ? $"Controlled by Windows slider ({b}%)"
                    : "Controlled by Windows slider";
            }
            if (!SupportsDdcCi) return "No brightness control";
            if (!Enabled) return "Disabled";
            var currentBrightness = _engine.LastInternalBrightness;
            if (currentBrightness < 0) return "\u2014";
            var t = _engine.CalculateTarget(DeviceName, _profile);
            return $"Target: {t}%  (internal {currentBrightness}%)";
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
        _onReset?.Invoke();
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
