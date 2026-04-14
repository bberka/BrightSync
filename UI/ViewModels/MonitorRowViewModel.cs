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
        _displayName = !string.IsNullOrWhiteSpace(monitor.FriendlyName)
            ? monitor.FriendlyName
            : monitor.Description;
        HardwareDescription = _displayName;
        ResolutionText = monitor.ResolutionWidth > 0
            ? $"{monitor.ResolutionWidth} × {monitor.ResolutionHeight}"
            : string.Empty;
        SupportsDdcCi = monitor.SupportsDdcCi;
        _profile = profile;
        _engine = engine;
        _enabled = profile.Enabled;
        _min = profile.MinBrightness;
        _max = profile.MaxBrightness;
        _multiplier = profile.Multiplier;
    }

    public void RefreshTargetText() => OnChanged(nameof(TargetText));

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}