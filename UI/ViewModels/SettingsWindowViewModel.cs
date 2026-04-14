using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;

namespace BrightSync.UI.ViewModels;

public sealed class SettingsWindowViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly BrightSyncEngine _engine;
    private readonly ConfigManager _config;
    private readonly DdcCiService _ddc;
    private bool _isUpdatingInternalBrightness;
    private System.Threading.Timer? _statusTimer;
    private System.Threading.Timer? _brightnessDebounce;

    public ObservableCollection<MonitorRowViewModel> Monitors { get; } = new();
    public bool HasMonitors => Monitors.Count > 0;

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

            if (_isUpdatingInternalBrightness) return;

            _brightnessDebounce?.Dispose();
            _brightnessDebounce = new System.Threading.Timer(_ =>
            {
                if (!_engine.TrySetInternalBrightness(_internalBrightness))
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        SetStatus("Using as virtual reference for external monitors.");
                    });
                }
            }, null, 300, System.Threading.Timeout.Infinite);
        }
    }

    public string InternalBrightnessText => $"{_internalBrightness}%";

    private int _enforcementInterval;
    public int EnforcementIntervalSeconds
    {
        get => _enforcementInterval;
        set
        {
            _enforcementInterval = Math.Clamp(value, 5, 300);
            _config.Config.EnforcementIntervalSeconds = _enforcementInterval;
            OnChanged();
        }
    }

    private bool _enforcementEnabled;
    public bool EnforcementEnabled
    {
        get => _enforcementEnabled;
        set
        {
            _enforcementEnabled = value;
            _config.Config.EnforcementEnabled = value;
            OnChanged();
        }
    }

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set { _startWithWindows = value; _config.Config.StartWithWindows = value; OnChanged(); }
    }

    public ICommand SaveCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ResetAllCommand { get; }

    public string StatusText { get; private set; } = string.Empty;
    public string EmptyStateText { get; private set; } = string.Empty;

    public SettingsWindowViewModel(
        BrightSyncEngine engine,
        ConfigManager config,
        DdcCiService ddc)
    {
        _engine = engine;
        _config = config;
        _ddc = ddc;

        var initial = engine.LastInternalBrightness;
        _internalBrightness = initial >= 0 ? initial : 50;
        _enforcementInterval = config.Config.EnforcementIntervalSeconds;
        _enforcementEnabled = config.Config.EnforcementEnabled;
        _startWithWindows = config.Config.StartWithWindows;

        SaveCommand = new RelayCommand(Save);
        RefreshCommand = new RelayCommand(Refresh);
        ResetAllCommand = new RelayCommand(ResetAll);

        engine.InternalBrightnessChanged += OnInternalBrightnessChanged;
        BuildMonitorList();
    }

    private void BuildMonitorList()
    {
        Monitors.Clear();
        foreach (var monitor in _ddc.GetMonitors())
        {
            var profile = _config.GetOrCreateProfile(monitor.DeviceName);
            Monitors.Add(new MonitorRowViewModel(monitor, profile, _engine, OnMonitorReset));
        }

        OnChanged(nameof(HasMonitors));
        if (Monitors.Count == 0)
            EmptyStateText = "No DDC/CI monitors detected. Try Refresh.";
        else
            EmptyStateText = string.Empty;
        OnChanged(nameof(EmptyStateText));
    }

    private void OnInternalBrightnessChanged(object? sender, int brightness)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _isUpdatingInternalBrightness = true;
            InternalBrightness = brightness >= 0 ? brightness : _internalBrightness;
            _isUpdatingInternalBrightness = false;
            foreach (var m in Monitors)
                m.RefreshTargetText();
        });
    }

    private void Save()
    {
        _config.Save();
        _engine.ForceSync();
        SetStatus($"Saved at {DateTime.Now:HH:mm:ss}");
    }

    private void Refresh()
    {
        SetStatus("Refreshing monitors\u2026");
        Task.Run(() =>
        {
            _engine.RefreshMonitors();
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                BuildMonitorList();
                SetStatus($"Found {Monitors.Count} monitor(s)");
            });
        });
    }

    private void ResetAll()
    {
        _config.Config.Monitors.Clear();
        EnforcementIntervalSeconds = new AppConfig().EnforcementIntervalSeconds;
        EnforcementEnabled = true;
        StartWithWindows = false;

        foreach (var monitor in Monitors)
            monitor.Reset();

        SetStatus("Reset all settings to defaults. Save to persist.");
    }

    private void OnMonitorReset()
    {
        SetStatus("Monitor settings reset. Save to persist.");
    }

    /// <summary>Sets the status text and auto-clears it after 10 seconds.</summary>
    private void SetStatus(string text)
    {
        StatusText = text;
        OnChanged(nameof(StatusText));

        _statusTimer?.Dispose();
        _statusTimer = new System.Threading.Timer(_ =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                StatusText = string.Empty;
                OnChanged(nameof(StatusText));
            });
        }, null, 10_000, System.Threading.Timeout.Infinite);
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _brightnessDebounce?.Dispose();
        _statusTimer?.Dispose();
        _engine.InternalBrightnessChanged -= OnInternalBrightnessChanged;
    }
}
