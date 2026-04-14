using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using BrightnessSync.Core.Brightness;
using BrightnessSync.Core.Config;
using BrightnessSync.Core.Monitors;

namespace BrightnessSync.UI;

// ---------------------------------------------------------------------------
// Minimal ICommand implementation — no framework dependency
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Value converters
// ---------------------------------------------------------------------------

public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? 1.0 : 0.38;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

// ---------------------------------------------------------------------------
// Relay command
// ---------------------------------------------------------------------------

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;
    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

// ---------------------------------------------------------------------------
// Per-monitor row in the settings list
// ---------------------------------------------------------------------------

public sealed class MonitorRowViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly MonitorProfile _profile;
    private readonly BrightnessSyncEngine _engine;

    public string DeviceName { get; }

    private string _friendlyName;
    public string FriendlyName
    {
        get => _friendlyName;
        set { _friendlyName = value; _profile.FriendlyName = value; OnChanged(); }
    }

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
    /// <summary>Best available name: custom label if set, else WMI friendly name.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(_friendlyName) ? _friendlyName : HardwareDescription;
    public string ResolutionText { get; }

    public string TargetText
    {
        get
        {
            if (!SupportsDdcCi) return "No DDC/CI";
            if (!Enabled) return "Disabled";
            int b = _engine.LastInternalBrightness;
            if (b < 0) return "—";
            int t = _engine.CalculateTarget(DeviceName, _profile);
            return $"Target: {t}%  (internal {b}%)";
        }
    }

    public MonitorRowViewModel(
        DdcMonitor monitor,
        MonitorProfile profile,
        BrightnessSyncEngine engine)
    {
        DeviceName = monitor.DeviceName;
        HardwareDescription = monitor.FriendlyName.Length > 0 ? monitor.FriendlyName : monitor.Description;
        ResolutionText = monitor.ResolutionWidth > 0
            ? $"{monitor.ResolutionWidth} × {monitor.ResolutionHeight}"
            : string.Empty;
        SupportsDdcCi = monitor.SupportsDdcCi;
        _profile = profile;
        _engine = engine;
        // Pre-populate friendly name from WMI if the user hasn't set a custom one
        if (string.IsNullOrWhiteSpace(profile.FriendlyName) && !string.IsNullOrEmpty(monitor.FriendlyName))
            profile.FriendlyName = monitor.FriendlyName;
        _friendlyName = profile.FriendlyName;
        _enabled = profile.Enabled;
        _min = profile.MinBrightness;
        _max = profile.MaxBrightness;
        _multiplier = profile.Multiplier;
    }

    public void RefreshTargetText() => OnChanged(nameof(TargetText));

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ---------------------------------------------------------------------------
// Main SettingsWindow view-model
// ---------------------------------------------------------------------------

public sealed class SettingsWindowViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly BrightnessSyncEngine _engine;
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

    public string StatusText { get; private set; } = string.Empty;

    public SettingsWindowViewModel(
        BrightnessSyncEngine engine,
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

        engine.InternalBrightnessChanged += OnInternalBrightnessChanged;
        BuildMonitorList();
    }

    private void BuildMonitorList()
    {
        Monitors.Clear();
        foreach (var monitor in _ddc.GetMonitors())
        {
            var profile = _config.GetOrCreateProfile(monitor.DeviceName, monitor.Description);
            Monitors.Add(new MonitorRowViewModel(monitor, profile, _engine));
        }

        OnChanged(nameof(HasMonitors));
        if (Monitors.Count == 0)
            StatusText = "No DDC/CI monitors detected — try Refresh.";
        else
            StatusText = string.Empty;
        OnChanged(nameof(StatusText));
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

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _engine.InternalBrightnessChanged -= OnInternalBrightnessChanged;
    }
}
