using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    public string DeviceName { get; }
    public string BrandName { get; }
    public string ModelName { get; }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; _profile.Enabled = value; OnChanged(); OnChanged(nameof(TargetText)); }
    }

    private int _min;
    public int MinBrightness
    {
        get => _min;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (value > _max) value = _max;
            _min = value;
            _profile.MinBrightness = value;
            OnChanged();
            OnChanged(nameof(TargetText));
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
            _max = value;
            _profile.MaxBrightness = value;
            OnChanged();
            OnChanged(nameof(TargetText));
        }
    }

    private double _multiplier;
    public double Multiplier
    {
        get => _multiplier;
        set
        {
            _multiplier = Math.Clamp(Math.Round(value, 2), 0.1, 3.0);
            _profile.Multiplier = _multiplier;
            OnChanged();
            OnChanged(nameof(MultiplierDisplay));
            OnChanged(nameof(TargetText));
        }
    }

    public string MultiplierDisplay => $"{_multiplier:F2}×";

    public bool SupportsDdcCi { get; }
    /// <summary>Raw DDC firmware string — shown as subtitle.</summary>
    public string HardwareDescription { get; }
    /// <summary>Best available detected monitor name for display only, never user-saved.</summary>
    public string DisplayName => _displayName;
    public string ResolutionText { get; }
    public string ConnectionText { get; }
    public string DdcStatusText => SupportsDdcCi ? "DDC/CI" : "No DDC/CI";
    public bool IsInternal { get; }

    public string TargetText
    {
        get
        {
            if (!SupportsDdcCi) return "No DDC/CI";
            if (!Enabled) return "Disabled";
            var b = _engine.LastInternalBrightness;
            if (b < 0) return "—";
            var t = _engine.CalculateTarget(DeviceName, _profile);
            return $"Target: {t}%  (internal {b}%)";
        }
    }

    public MonitorRowViewModel(
        DdcMonitor monitor,
        MonitorProfile profile,
        BrightSyncEngine engine)
    {
        DeviceName = monitor.DeviceName;
        BrandName = monitor.ManufacturerName;
        ModelName = monitor.ModelName;
        _displayName = BuildDisplayName(monitor);
        HardwareDescription = BuildHardwareDescription(monitor, _displayName);
        ResolutionText = BuildResolutionText(monitor);
        ConnectionText = monitor.ConnectionType;
        IsInternal = monitor.IsInternal;
        SupportsDdcCi = monitor.SupportsDdcCi;
        _profile = profile;
        _engine = engine;
        _enabled = profile.Enabled;
        _min = profile.MinBrightness;
        _max = profile.MaxBrightness;
        _multiplier = profile.Multiplier;
    }

    public void RefreshTargetText() => OnChanged(nameof(TargetText));

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
