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
    private readonly EyeProtectionService _eyeProtection;
    private readonly BrightnessBoostService _brightnessBoost;
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
    public bool IsIdleReductionActive => _engine.IsIdleReductionActive;
    public string IdleReductionStatusText => _engine.IsIdleReductionActive
        ? "Idle dimming is active."
        : string.Empty;

    public bool EyeProtectionEnabled
    {
        get => _config.Config.EyeProtectionEnabled;
        set
        {
            if (_config.Config.EyeProtectionEnabled == value)
                return;

            _eyeProtection.SetEnabled(value);
            OnChanged();
            OnChanged(nameof(EyeProtectionStatusText));
            Refresh();
        }
    }

    public string EyeProtectionStatusText
    {
        get
        {
            if (!EyeProtectionEnabled)
                return "Eye protection is off.";

            var endUtc = _eyeProtection.EndTimeUtc;
            var timeText = endUtc.HasValue ? $" Ends at {endUtc.Value.ToLocalTime():HH:mm}." : string.Empty;
            return $"Eye protection active (-{_config.Config.EyeProtectionReductionPercent}%).{timeText}";
        }
    }

    public bool BrightnessBoostEnabled
    {
        get => _config.Config.BrightnessBoostEnabled;
        set
        {
            if (_config.Config.BrightnessBoostEnabled == value)
                return;

            _brightnessBoost.SetEnabled(value);
            OnChanged();
            OnChanged(nameof(BrightnessBoostStatusText));
            Refresh();
        }
    }

    public string BrightnessBoostStatusText
    {
        get
        {
            if (!BrightnessBoostEnabled)
                return "Brightness boost is off.";

            var endUtc = _brightnessBoost.EndTimeUtc;
            var timeText = endUtc.HasValue ? $" Ends at {endUtc.Value.ToLocalTime():HH:mm}." : string.Empty;
            return $"Brightness boost active (+{_config.Config.BrightnessBoostPercent}%).{timeText}";
        }
    }

    public ICommand OpenSettingsCommand { get; }

    public QuickBrightnessViewModel(
        BrightSyncEngine engine,
        AutoBrightnessService autoBrightness,
        EyeProtectionService eyeProtection,
        BrightnessBoostService brightnessBoost,
        DdcCiService ddc,
        ConfigManager config,
        Action openSettings)
    {
        _engine = engine;
        _autoBrightness = autoBrightness;
        _eyeProtection = eyeProtection;
        _brightnessBoost = brightnessBoost;
        _ddc = ddc;
        _config = config;

        var initial = engine.LastInternalBrightness;
        _internalBrightness = initial >= 0 ? initial : 50;

        OpenSettingsCommand = new RelayCommand(openSettings);

        engine.InternalBrightnessChanged += OnBrightnessChanged;
        engine.TargetsChanged += OnTargetsChanged;
        autoBrightness.StateChanged += OnAutoBrightnessChanged;
        eyeProtection.StateChanged += OnEyeProtectionChanged;
        brightnessBoost.StateChanged += OnBrightnessBoostChanged;
        RefreshMonitorTargets();
    }

    private void OnEyeProtectionChanged(object? sender, bool e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(Refresh);
    }

    private void OnBrightnessBoostChanged(object? sender, bool e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(Refresh);
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
        OnChanged(nameof(EyeProtectionEnabled));
        OnChanged(nameof(EyeProtectionStatusText));
        OnChanged(nameof(BrightnessBoostEnabled));
        OnChanged(nameof(BrightnessBoostStatusText));
        OnChanged(nameof(IsIdleReductionActive));
        OnChanged(nameof(IdleReductionStatusText));
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

    private void OnTargetsChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            OnChanged(nameof(IsIdleReductionActive));
            OnChanged(nameof(IdleReductionStatusText));
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
        _engine.TargetsChanged -= OnTargetsChanged;
        _autoBrightness.StateChanged -= OnAutoBrightnessChanged;
        _eyeProtection.StateChanged -= OnEyeProtectionChanged;
        _brightnessBoost.StateChanged -= OnBrightnessBoostChanged;
        Log.Debug("Disposed quick brightness view model");
    }
}

public record MonitorTargetInfo(string Name, string TargetText);
