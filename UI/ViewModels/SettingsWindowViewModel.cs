using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using BrightSync.Core.Updates;
using Serilog;

namespace BrightSync.UI.ViewModels;

public sealed class SettingsWindowViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? AutoBrightnessCurveChanged;

    private readonly BrightSyncEngine _engine;
    private readonly AutoBrightnessService _autoBrightness;
    private readonly IdleReductionService _idleReduction;
    private readonly ConfigManager _config;
    private readonly DdcCiService _ddc;
    private readonly UpdateChecker _updateChecker;
    private bool _isUpdatingInternalBrightness;
    private bool _isCheckingForUpdates;
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

            if (_isUpdatingInternalBrightness || AutoBrightnessEnabled) return;

            _brightnessDebounce?.Dispose();
            _brightnessDebounce = new System.Threading.Timer(_ =>
            {
                Log.Debug("Settings window requested internal brightness change to {Brightness}%", _internalBrightness);
                if (!_engine.TrySetUserBrightness(_internalBrightness))
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        SetStatus(AutoBrightnessEnabled
                            ? "Automatic brightness is controlling the slider."
                            : "Using as virtual reference for external monitors.");
                    });
                }
            }, null, 300, System.Threading.Timeout.Infinite);
        }
    }

    public string InternalBrightnessText => $"{_internalBrightness}%";
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
            if (value)
            {
                _isUpdatingInternalBrightness = true;
                InternalBrightness = _autoBrightness.GetCurrentBrightness();
                _isUpdatingInternalBrightness = false;
            }

            foreach (var monitor in Monitors)
                monitor.RefreshTargetText();
        }
    }

    public bool IsManualBrightnessEnabled => !AutoBrightnessEnabled;
    public string AutoBrightnessStatusText
    {
        get
        {
            if (!AutoBrightnessEnabled)
                return "Automatic brightness is off.";

            var now = DateTime.Now;
            return $"Automatic brightness is on. Current {InternalBrightnessText} at {now:HH:mm}.";
        }
    }

    public IReadOnlyList<AutoBrightnessControlPoint> AutoBrightnessCurvePoints => _config.Config.AutoBrightness.Curve;
    public string AutoBrightnessPreviewText
    {
        get
        {
            var brightness = _autoBrightness.GetCurrentBrightness();
            return $"Now {DateTime.Now:HH:mm} -> {brightness}%";
        }
    }

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

    private bool _disableMonitorAccessWhileLocked;
    public bool DisableMonitorAccessWhileLocked
    {
        get => _disableMonitorAccessWhileLocked;
        set
        {
            _disableMonitorAccessWhileLocked = value;
            _config.Config.DisableMonitorAccessWhileLocked = value;
            OnChanged();
        }
    }

    private bool _idleReductionEnabled;
    public bool IdleReductionEnabled
    {
        get => _idleReductionEnabled;
        set
        {
            if (_idleReductionEnabled == value)
                return;

            _idleReductionEnabled = value;
            _config.Config.IdleReductionEnabled = value;
            OnChanged();
            OnChanged(nameof(IdleReductionStatusText));
            _idleReduction.ReevaluateNow();
            RefreshTargets();
        }
    }

    private int _idleTimeoutMinutes;
    public int IdleTimeoutMinutes
    {
        get => _idleTimeoutMinutes;
        set
        {
            var clamped = Math.Clamp(value, 1, 120);
            if (_idleTimeoutMinutes == clamped)
                return;

            _idleTimeoutMinutes = clamped;
            _config.Config.IdleTimeoutMinutes = clamped;
            OnChanged();
            OnChanged(nameof(IdleTimeoutText));
            OnChanged(nameof(IdleReductionStatusText));
            _idleReduction.ReevaluateNow();
        }
    }

    public string IdleTimeoutText => $"{_idleTimeoutMinutes} min";

    private bool _idleReductionToMinimum;
    public bool IdleReductionToMinimum
    {
        get => _idleReductionToMinimum;
        set
        {
            if (_idleReductionToMinimum == value)
                return;

            _idleReductionToMinimum = value;
            _config.Config.IdleReductionToMinimum = value;
            OnChanged();
            OnChanged(nameof(IsIdleReductionPercentVisible));
            OnChanged(nameof(IdleReductionStatusText));
            if (_engine.IsIdleReductionActive)
                _engine.ForceSync();
            else
                RefreshTargets();
        }
    }

    public bool IsIdleReductionPercentVisible => !_idleReductionToMinimum;

    private int _idleReductionPercent;
    public int IdleReductionPercent
    {
        get => _idleReductionPercent;
        set
        {
            var clamped = Math.Clamp(value, 10, 100);
            if (_idleReductionPercent == clamped)
                return;

            _idleReductionPercent = clamped;
            _config.Config.IdleReductionPercent = clamped;
            OnChanged();
            OnChanged(nameof(IdleReductionPercentText));
            OnChanged(nameof(IdleReductionStatusText));
            if (_engine.IsIdleReductionActive)
                _engine.ForceSync();
            else
                RefreshTargets();
        }
    }

    public string IdleReductionPercentText => $"{_idleReductionPercent}%";

    private bool _idleIgnoreMediaPlayback;
    public bool IdleIgnoreMediaPlayback
    {
        get => _idleIgnoreMediaPlayback;
        set
        {
            if (_idleIgnoreMediaPlayback == value)
                return;

            _idleIgnoreMediaPlayback = value;
            _config.Config.IdleIgnoreMediaPlayback = value;
            OnChanged();
            OnChanged(nameof(IdleReductionStatusText));
            _idleReduction.ReevaluateNow();
        }
    }

    public string IdleReductionStatusText
    {
        get
        {
            if (!IdleReductionEnabled)
                return "Idle dimming is off.";

            var action = IdleReductionToMinimum
                ? "set external monitor targets to their configured minimum"
                : $"scale external monitor targets to {IdleReductionPercentText}";
            var mediaText = IdleIgnoreMediaPlayback ? " Media playback pauses idle dimming." : string.Empty;
            var activeText = _engine.IsIdleReductionActive ? " Active now." : string.Empty;
            return $"After {IdleTimeoutText} of no input, BrightSync will {action}.{mediaText}{activeText}";
        }
    }

    public ICommand SaveCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ResetAllCommand { get; }
    public ICommand ResetCurveCommand { get; }
    public RelayCommand CheckForUpdatesCommand { get; }

    public string StatusText { get; private set; } = string.Empty;
    public string EmptyStateText { get; private set; } = string.Empty;

    public SettingsWindowViewModel(
        BrightSyncEngine engine,
        AutoBrightnessService autoBrightness,
        IdleReductionService idleReduction,
        ConfigManager config,
        DdcCiService ddc,
        UpdateChecker updateChecker)
    {
        _engine = engine;
        _autoBrightness = autoBrightness;
        _idleReduction = idleReduction;
        _config = config;
        _ddc = ddc;
        _updateChecker = updateChecker;

        var initial = engine.LastInternalBrightness;
        _internalBrightness = initial >= 0 ? initial : 50;
        _enforcementInterval = config.Config.EnforcementIntervalSeconds;
        _enforcementEnabled = config.Config.EnforcementEnabled;
        _startWithWindows = config.Config.StartWithWindows;
        _disableMonitorAccessWhileLocked = config.Config.DisableMonitorAccessWhileLocked;
        _idleReductionEnabled = config.Config.IdleReductionEnabled;
        _idleTimeoutMinutes = Math.Clamp(config.Config.IdleTimeoutMinutes, 1, 120);
        _idleReductionToMinimum = config.Config.IdleReductionToMinimum;
        _idleReductionPercent = Math.Clamp(config.Config.IdleReductionPercent, 10, 100);
        _idleIgnoreMediaPlayback = config.Config.IdleIgnoreMediaPlayback;

        SaveCommand = new RelayCommand(Save);
        RefreshCommand = new RelayCommand(Refresh);
        ResetAllCommand = new RelayCommand(ResetAll);
        ResetCurveCommand = new RelayCommand(ResetCurve);
        CheckForUpdatesCommand = new RelayCommand(CheckForUpdates, () => !_isCheckingForUpdates);

        engine.InternalBrightnessChanged += OnInternalBrightnessChanged;
        engine.TargetsChanged += OnTargetsChanged;
        autoBrightness.StateChanged += OnAutoBrightnessChanged;
        idleReduction.StateChanged += OnIdleReductionChanged;
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
            EmptyStateText = "No displays detected. Try Refresh.";
        else
            EmptyStateText = string.Empty;
        OnChanged(nameof(EmptyStateText));
        Log.Information("Settings monitor list rebuilt. MonitorCount={MonitorCount}", Monitors.Count);
    }

    private void OnInternalBrightnessChanged(object? sender, int brightness)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _isUpdatingInternalBrightness = true;
            InternalBrightness = brightness >= 0 ? brightness : _internalBrightness;
            _isUpdatingInternalBrightness = false;
            OnChanged(nameof(AutoBrightnessStatusText));
            OnChanged(nameof(AutoBrightnessPreviewText));
            foreach (var m in Monitors)
                m.RefreshTargetText();
        });
    }

    private void Save()
    {
        _config.Save();
        _engine.ForceSync();
        _autoBrightness.RecalculateNow();
        SetStatus($"Saved at {DateTime.Now:HH:mm:ss}");
        Log.Information("Settings saved from UI");
    }

    private void Refresh()
    {
        Log.Information("Settings UI requested monitor refresh");
        SetStatus("Refreshing monitors...");
        Task.Run(() =>
        {
            _engine.RefreshMonitors();
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                RefreshMonitorList("Found {0} monitor(s)");
            });
        });
    }

    public void RefreshMonitorList(string? statusFormat = null)
    {
        BuildMonitorList();
        RefreshTargets();

        if (!string.IsNullOrWhiteSpace(statusFormat))
            SetStatus(string.Format(statusFormat, Monitors.Count));
    }

    private void ResetAll()
    {
        _config.Config.Monitors.Clear();
        _config.Config.AutoBrightness = AutoBrightnessSettings.CreateDefault();
        EnforcementIntervalSeconds = new AppConfig().EnforcementIntervalSeconds;
        EnforcementEnabled = true;
        DisableMonitorAccessWhileLocked = false;
        IdleReductionEnabled = false;
        IdleTimeoutMinutes = new AppConfig().IdleTimeoutMinutes;
        IdleReductionToMinimum = false;
        IdleReductionPercent = new AppConfig().IdleReductionPercent;
        IdleIgnoreMediaPlayback = true;
        StartWithWindows = false;

        foreach (var monitor in Monitors)
            monitor.Reset();

        OnChanged(nameof(AutoBrightnessEnabled));
        OnChanged(nameof(IsManualBrightnessEnabled));
        OnChanged(nameof(AutoBrightnessStatusText));
        OnChanged(nameof(AutoBrightnessCurvePoints));
        OnChanged(nameof(AutoBrightnessPreviewText));
        OnChanged(nameof(IdleReductionStatusText));
        OnChanged(nameof(IsIdleReductionPercentVisible));
        OnChanged(nameof(IdleTimeoutText));
        OnChanged(nameof(IdleReductionPercentText));
        AutoBrightnessCurveChanged?.Invoke(this, EventArgs.Empty);
        SetStatus("Reset all settings to defaults. Save to persist.");
        Log.Warning("All settings were reset to defaults in the UI");
    }

    public void UpdateAutoBrightnessPoint(int index, int brightness)
    {
        var curve = _config.Config.AutoBrightness.Curve;
        if (index < 0 || index >= curve.Count)
            return;

        brightness = Math.Clamp(brightness, 0, 100);
        curve[index].Brightness = brightness;
        if (index == 0)
            curve[^1].Brightness = brightness;
        else if (index == curve.Count - 1)
            curve[0].Brightness = brightness;

        OnChanged(nameof(AutoBrightnessCurvePoints));
        OnChanged(nameof(AutoBrightnessStatusText));
        OnChanged(nameof(AutoBrightnessPreviewText));
        AutoBrightnessCurveChanged?.Invoke(this, EventArgs.Empty);
        _autoBrightness.RecalculateNow();
        foreach (var monitor in Monitors)
            monitor.RefreshTargetText();
    }

    private void ResetCurve()
    {
        var currentEnabled = _config.Config.AutoBrightness.Enabled;
        _config.Config.AutoBrightness = AutoBrightnessSettings.CreateDefault();
        _config.Config.AutoBrightness.Enabled = currentEnabled;

        OnChanged(nameof(AutoBrightnessEnabled));
        OnChanged(nameof(IsManualBrightnessEnabled));
        OnChanged(nameof(AutoBrightnessStatusText));
        OnChanged(nameof(AutoBrightnessCurvePoints));
        OnChanged(nameof(AutoBrightnessPreviewText));
        AutoBrightnessCurveChanged?.Invoke(this, EventArgs.Empty);
        _autoBrightness.RecalculateNow();
        foreach (var monitor in Monitors)
            monitor.RefreshTargetText();

        SetStatus("Automatic brightness curve reset. Save to persist.");
        Log.Information("Automatic brightness curve reset in the UI");
    }

    public IReadOnlyList<string> GetCurveHourLabels()
    {
        return ["0", "3", "6", "9", "12", "15", "18", "21", "24"];
    }

    private void OnAutoBrightnessChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _isUpdatingInternalBrightness = true;
            InternalBrightness = _autoBrightness.GetCurrentBrightness();
            _isUpdatingInternalBrightness = false;
            OnChanged(nameof(AutoBrightnessEnabled));
            OnChanged(nameof(IsManualBrightnessEnabled));
            OnChanged(nameof(AutoBrightnessStatusText));
            OnChanged(nameof(AutoBrightnessPreviewText));
            AutoBrightnessCurveChanged?.Invoke(this, EventArgs.Empty);
            foreach (var monitor in Monitors)
                monitor.RefreshTargetText();
        });
    }

    private void OnTargetsChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            RefreshTargets();
            OnChanged(nameof(IdleReductionStatusText));
        });
    }

    private void OnIdleReductionChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            RefreshTargets();
            OnChanged(nameof(IdleReductionStatusText));
        });
    }

    private void RefreshTargets()
    {
        foreach (var monitor in Monitors)
            monitor.RefreshTargetText();
    }

    private void CheckForUpdates()
    {
        if (_isCheckingForUpdates)
            return;

        _isCheckingForUpdates = true;
        CheckForUpdatesCommand.Raise();
        SetStatus("Checking for updates...");

        Task.Run(async () =>
        {
            var result = await _updateChecker.CheckNowAsync(force: true);
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _isCheckingForUpdates = false;
                CheckForUpdatesCommand.Raise();
                SetStatus(BuildUpdateStatusText(result));
            });
        });
    }

    private static string BuildUpdateStatusText(UpdateCheckResult result)
    {
        return result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable =>
                $"Update available: v{result.LatestVersion} (current v{result.CurrentVersion}). Opening releases page.",
            UpdateCheckStatus.UpToDate =>
                result.CurrentVersion is null
                    ? "You are up to date."
                    : $"You are up to date (v{result.CurrentVersion}).",
            UpdateCheckStatus.LatestVersionUnavailable =>
                "Unable to determine the latest version from GitHub.",
            UpdateCheckStatus.Failed =>
                $"Update check failed: {result.Error?.Message ?? "Unknown error."}",
            UpdateCheckStatus.SkippedAlreadyCheckedToday =>
                $"Update check already ran today ({result.LastCheckedDate:yyyy-MM-dd}).",
            _ => "Update check finished."
        };
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
        _engine.TargetsChanged -= OnTargetsChanged;
        _autoBrightness.StateChanged -= OnAutoBrightnessChanged;
        _idleReduction.StateChanged -= OnIdleReductionChanged;
        Log.Debug("Disposed settings window view model");
    }
}
