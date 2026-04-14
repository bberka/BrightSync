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

    public ObservableCollection<MonitorRowViewModel> Monitors { get; } = new();
    public bool HasMonitors => Monitors.Count > 0;

    private int _internalBrightness;
    public int InternalBrightness
    {
        get => _internalBrightness;
        private set { _internalBrightness = value; OnChanged(); OnChanged(nameof(InternalBrightnessText)); }
    }

    public string InternalBrightnessText =>
        _internalBrightness >= 0
            ? $"{_internalBrightness}%"
            : "Not available (desktop-only setup?)";

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

        _internalBrightness = engine.LastInternalBrightness;
        _enforcementInterval = config.Config.EnforcementIntervalSeconds;
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
            InternalBrightness = brightness;
            foreach (var m in Monitors)
                m.RefreshTargetText();
        });
    }

    private void Save()
    {
        _config.Save();
        _engine.ForceSync();
        StatusText = $"Saved at {DateTime.Now:HH:mm:ss}";
        OnChanged(nameof(StatusText));
    }

    private void Refresh()
    {
        StatusText = "Refreshing monitors…";
        OnChanged(nameof(StatusText));
        Task.Run(() =>
        {
            _engine.RefreshMonitors();
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                BuildMonitorList();
                StatusText = $"Found {Monitors.Count} monitor(s)";
                OnChanged(nameof(StatusText));
            });
        });
    }

    private void ResetAll()
    {
        _config.Config.Monitors.Clear();
        EnforcementIntervalSeconds = new AppConfig().EnforcementIntervalSeconds;
        StartWithWindows = false;

        foreach (var monitor in Monitors)
            monitor.Reset();

        StatusText = "Reset all settings to defaults. Save to persist.";
        OnChanged(nameof(StatusText));
    }

    private void OnMonitorReset()
    {
        StatusText = "Monitor settings reset. Save to persist.";
        OnChanged(nameof(StatusText));
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _engine.InternalBrightnessChanged -= OnInternalBrightnessChanged;
    }
}
