using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;

namespace BrightSync.UI.ViewModels;

public sealed class QuickBrightnessViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly BrightSyncEngine _engine;
    private readonly DdcCiService _ddc;
    private readonly ConfigManager _config;
    private bool _isUpdating;
    private System.Threading.Timer? _brightnessDebounce;

    public ObservableCollection<MonitorTargetInfo> MonitorTargets { get; } = new();

    private int _internalBrightness;
    public int InternalBrightness
    {
        get => _internalBrightness;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (_internalBrightness == clamped) return;
            _internalBrightness = clamped;
            OnChanged();
            OnChanged(nameof(InternalBrightnessText));

            if (!_isUpdating)
            {
                _brightnessDebounce?.Dispose();
                _brightnessDebounce = new System.Threading.Timer(_ =>
                {
                    _engine.TrySetInternalBrightness(_internalBrightness);
                }, null, 300, System.Threading.Timeout.Infinite);
            }
        }
    }

    public bool HasInternalBrightnessControl => true;

    public string InternalBrightnessText => $"{_internalBrightness}%";

    public ICommand OpenSettingsCommand { get; }

    public QuickBrightnessViewModel(
        BrightSyncEngine engine,
        DdcCiService ddc,
        ConfigManager config,
        Action openSettings)
    {
        _engine = engine;
        _ddc = ddc;
        _config = config;

        var initial = engine.LastInternalBrightness;
        _internalBrightness = initial >= 0 ? initial : 50;

        OpenSettingsCommand = new RelayCommand(openSettings);

        engine.InternalBrightnessChanged += OnBrightnessChanged;
        RefreshMonitorTargets();
    }

    /// <summary>Refreshes brightness + monitor targets — call when showing the popup.</summary>
    public void Refresh()
    {
        _isUpdating = true;
        var b = _engine.LastInternalBrightness;
        InternalBrightness = b >= 0 ? b : _internalBrightness;
        _isUpdating = false;
        RefreshMonitorTargets();
    }

    private void RefreshMonitorTargets()
    {
        MonitorTargets.Clear();
        foreach (var monitor in _ddc.GetMonitors())
        {
            if (!monitor.SupportsDdcCi || monitor.IsInternal) continue;
            var profile = _config.GetOrCreateProfile(monitor.DeviceName);
            if (!profile.Enabled) continue;
            var target = _engine.CalculateTarget(monitor.DeviceName, profile);
            MonitorTargets.Add(new MonitorTargetInfo(
                BuildDisplayName(monitor),
                target >= 0 ? $"{target}%" : "\u2014"));
        }
    }

    private void OnBrightnessChanged(object? sender, int brightness)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _isUpdating = true;
            InternalBrightness = brightness >= 0 ? brightness : _internalBrightness;
            _isUpdating = false;
            RefreshMonitorTargets();
        });
    }

    private static string BuildDisplayName(DdcMonitor monitor)
    {
        if (!string.IsNullOrWhiteSpace(monitor.ManufacturerName) &&
            !string.IsNullOrWhiteSpace(monitor.ModelName))
            return $"{monitor.ManufacturerName} {monitor.ModelName}";
        if (!string.IsNullOrWhiteSpace(monitor.FriendlyName))
            return monitor.FriendlyName;
        return monitor.Description;
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _brightnessDebounce?.Dispose();
        _engine.InternalBrightnessChanged -= OnBrightnessChanged;
    }
}

public record MonitorTargetInfo(string Name, string TargetText);
