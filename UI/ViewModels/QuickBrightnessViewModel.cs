using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using Serilog;

namespace BrightSync.UI.ViewModels;

public sealed class QuickBrightnessViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly BrightSyncEngine _engine;
    private readonly AutoBrightnessService _autoBrightness;
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

            if (!_isUpdating && !AutoBrightnessEnabled)
            {
                _brightnessDebounce?.Dispose();
                _brightnessDebounce = new System.Threading.Timer(_ =>
                {
                    Log.Debug("Quick popup requested internal brightness change to {Brightness}%", _internalBrightness);
                    _engine.TrySetUserBrightness(_internalBrightness);
                }, null, 300, System.Threading.Timeout.Infinite);
            }
        }
    }

    public bool HasInternalBrightnessControl => true;
    public bool AutoBrightnessEnabled
    {
        get => _config.Config.AutoBrightness.Enabled;
        set
        {
            if (_config.Config.AutoBrightness.Enabled == value)
                return;

            _autoBrightness.SetEnabled(value);
            OnChanged();
            OnChanged(nameof(IsManualBrightnessEnabled));
            OnChanged(nameof(AutoBrightnessStatusText));
            Refresh();
        }
    }

    public bool IsManualBrightnessEnabled => !AutoBrightnessEnabled;

    public string InternalBrightnessText => $"{_internalBrightness}%";
    public string AutoBrightnessStatusText => AutoBrightnessEnabled
        ? $"Automatic brightness is on. Current {InternalBrightnessText}."
        : "Automatic brightness is off.";

    public ICommand OpenSettingsCommand { get; }

    public QuickBrightnessViewModel(
        BrightSyncEngine engine,
        AutoBrightnessService autoBrightness,
        DdcCiService ddc,
        ConfigManager config,
        Action openSettings)
    {
        _engine = engine;
        _autoBrightness = autoBrightness;
        _ddc = ddc;
        _config = config;

        var initial = engine.LastInternalBrightness;
        _internalBrightness = initial >= 0 ? initial : 50;

        OpenSettingsCommand = new RelayCommand(openSettings);

        engine.InternalBrightnessChanged += OnBrightnessChanged;
        autoBrightness.StateChanged += OnAutoBrightnessChanged;
        RefreshMonitorTargets();
    }

    /// <summary>Refreshes brightness + monitor targets — call when showing the popup.</summary>
    public void Refresh()
    {
        Log.Debug("Refreshing quick brightness view model");
        _isUpdating = true;
        var b = AutoBrightnessEnabled ? _autoBrightness.GetCurrentBrightness() : _engine.LastInternalBrightness;
        InternalBrightness = b >= 0 ? b : _internalBrightness;
        _isUpdating = false;
        OnChanged(nameof(AutoBrightnessEnabled));
        OnChanged(nameof(IsManualBrightnessEnabled));
        OnChanged(nameof(AutoBrightnessStatusText));
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

    private void OnAutoBrightnessChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(Refresh);
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
        _autoBrightness.StateChanged -= OnAutoBrightnessChanged;
        Log.Debug("Disposed quick brightness view model");
    }
}

public record MonitorTargetInfo(string Name, string TargetText);
