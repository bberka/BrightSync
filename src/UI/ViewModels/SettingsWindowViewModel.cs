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
    private readonly EyeProtectionService _eyeProtection;
    private readonly BrightnessBoostService _brightnessBoost;
    private readonly ConfigManager _config;
    private readonly DdcCiService _ddc;
    private readonly UpdateChecker _updateChecker;
    private bool _isUpdatingInternalBrightness;
    private bool _isCheckingForUpdates;
    private bool _suspendAutoSave;
    private System.Threading.Timer? _statusTimer;
    private System.Threading.Timer? _brightnessDebounce;
    private System.Threading.Timer? _autoSaveDebounce;

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
                    Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
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
            OnChanged(nameof(ManualBrightnessDisabledReasonText));
            OnChanged(nameof(AutoBrightnessStatusText));
            if (value)
            {
                _isUpdatingInternalBrightness = true;
                InternalBrightness = _autoBrightness.GetCurrentBrightness();
                _isUpdatingInternalBrightness = false;
            }

            foreach (var monitor in Monitors)
                monitor.RefreshTargetText();

            RequestAutoSave();
        }
    }

    public bool IsManualBrightnessEnabled => !AutoBrightnessEnabled;

    public string ManualBrightnessDisabledReasonText => AutoBrightnessEnabled
        ? "This slider is disabled because automatic brightness is controlling the shared brightness value. Scroll down to adjust the curve, or disable automatic brightness to edit it manually."
        : string.Empty;

    public bool AutoBrightnessLockEnabled
    {
        get => _config.Config.AutoBrightness.LockWhenManualBrightnessChanges;
        set
        {
            if (_config.Config.AutoBrightness.LockWhenManualBrightnessChanges == value)
                return;

            _config.Config.AutoBrightness.LockWhenManualBrightnessChanges = value;
            OnChanged();
            OnChanged(nameof(AutoBrightnessStatusText));
            OnChanged(nameof(AutoBrightnessLockDescription));
            RequestAutoSave();
        }
    }

    public string AutoBrightnessStatusText
    {
        get
        {
            if (!AutoBrightnessEnabled)
                return "Automatic brightness is off.";

            var now = DateTime.Now;
            var lockText = AutoBrightnessLockEnabled ? " Locked against manual brightness changes." : string.Empty;
            return $"Automatic brightness is on. Current {InternalBrightnessText} at {now:HH:mm}.{lockText}";
        }
    }

    public string AutoBrightnessLockDescription => AutoBrightnessLockEnabled
        ? "Manual brightness changes will not turn off automatic brightness. Disable it from the app when needed."
        : "When enabled, BrightSync turns off automatic brightness after a manual Windows brightness change.";

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
            if (_config.Config.EnforcementIntervalSeconds == _enforcementInterval)
                return;

            _config.Config.EnforcementIntervalSeconds = _enforcementInterval;
            OnChanged();
            RequestAutoSave(debounce: true);
        }
    }

    private bool _enforcementEnabled;

    public bool EnforcementEnabled
    {
        get => _enforcementEnabled;
        set
        {
            if (_enforcementEnabled == value)
                return;

            _enforcementEnabled = value;
            _config.Config.EnforcementEnabled = value;
            OnChanged();
            RequestAutoSave();
        }
    }

    private bool _startWithWindows;

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value)
                return;

            _startWithWindows = value;
            _config.Config.StartWithWindows = value;
            OnChanged();
            RequestAutoSave();
        }
    }

    private bool _useLegacyDdcCiDetection;

    public bool UseLegacyDdcCiDetection
    {
        get => _useLegacyDdcCiDetection;
        set
        {
            if (_useLegacyDdcCiDetection == value)
                return;

            _useLegacyDdcCiDetection = value;
            _config.Config.UseLegacyDdcCiDetection = value;
            OnChanged();
            RequestAutoSave(
                statusText:
                "DDC/CI detection mode changed and saved. Refresh monitors or restart the app to apply it.");
        }
    }

    private bool _energySaverReductionEnabled;

    public bool EnergySaverReductionEnabled
    {
        get => _energySaverReductionEnabled;
        set
        {
            if (_energySaverReductionEnabled == value)
                return;

            _energySaverReductionEnabled = value;
            _config.Config.EnergySaverReductionEnabled = value;
            OnChanged();
            OnChanged(nameof(EnergySaverStatusText));
            RequestAutoSave();
        }
    }

    private int _energySaverReductionPercent;

    public int EnergySaverReductionPercent
    {
        get => _energySaverReductionPercent;
        set
        {
            var clamped = Math.Clamp(value, 5, 50);
            if (_energySaverReductionPercent == clamped)
                return;

            _energySaverReductionPercent = clamped;
            _config.Config.EnergySaverReductionPercent = clamped;
            OnChanged();
            OnChanged(nameof(EnergySaverStatusText));
            RefreshTargets();
            RequestAutoSave(debounce: true);
        }
    }

    public string EnergySaverStatusText
    {
        get
        {
            if (!EnergySaverReductionEnabled)
                return "Energy saver reduction is off.";

            return
                $"Brightness will decrease by {EnergySaverReductionPercent} percentage points when Windows Energy Saver is active.";
        }
    }

    private bool _eyeProtectionEnabled;

    public bool EyeProtectionEnabled
    {
        get => _eyeProtectionEnabled;
        set
        {
            if (_eyeProtectionEnabled == value)
                return;

            _eyeProtectionEnabled = value;
            _eyeProtection.SetEnabled(value);
            OnChanged();
            OnChanged(nameof(EyeProtectionStatusText));
        }
    }

    private int _eyeProtectionReductionPercent;

    public int EyeProtectionReductionPercent
    {
        get => _eyeProtectionReductionPercent;
        set
        {
            var clamped = Math.Clamp(value, 5, 80);
            if (_eyeProtectionReductionPercent == clamped)
                return;

            _eyeProtectionReductionPercent = clamped;
            _config.Config.EyeProtectionReductionPercent = clamped;
            OnChanged();
            OnChanged(nameof(EyeProtectionStatusText));
            RefreshTargets();
            RequestAutoSave(debounce: true);
        }
    }

    private int _eyeProtectionDefaultDurationHours;

    public int EyeProtectionDefaultDurationHours
    {
        get => _eyeProtectionDefaultDurationHours;
        set
        {
            var clamped = Math.Clamp(value, 1, 24);
            if (_eyeProtectionDefaultDurationHours == clamped)
                return;

            _eyeProtectionDefaultDurationHours = clamped;
            _config.Config.EyeProtectionDefaultDurationHours = clamped;
            OnChanged();
            OnChanged(nameof(EyeProtectionStatusText));
            RequestAutoSave(debounce: true);
        }
    }

    public string EyeProtectionStatusText
    {
        get
        {
            var reduction = $"{EyeProtectionReductionPercent}%";
            var duration = _eyeProtectionDefaultDurationHours == 1
                ? "1 hour"
                : $"{_eyeProtectionDefaultDurationHours} hours";

            if (!EyeProtectionEnabled)
                return $"Eye protection will subtract {reduction} from brightness for {duration} when enabled.";

            var endUtc = _eyeProtection.EndTimeUtc;
            var timeText = endUtc.HasValue ? $"Ends at {endUtc.Value.ToLocalTime():HH:mm}." : string.Empty;
            return $"Eye protection is active (-{reduction}). {timeText}";
        }
    }

    private bool _brightnessBoostEnabled;

    public bool BrightnessBoostEnabled
    {
        get => _brightnessBoostEnabled;
        set
        {
            if (_brightnessBoostEnabled == value)
                return;

            _brightnessBoostEnabled = value;
            _brightnessBoost.SetEnabled(value);
            OnChanged();
            OnChanged(nameof(BrightnessBoostStatusText));
        }
    }

    private int _brightnessBoostPercent;

    public int BrightnessBoostPercent
    {
        get => _brightnessBoostPercent;
        set
        {
            var clamped = Math.Clamp(value, 5, 100);
            if (_brightnessBoostPercent == clamped)
                return;

            _brightnessBoostPercent = clamped;
            _config.Config.BrightnessBoostPercent = clamped;
            OnChanged();
            OnChanged(nameof(BrightnessBoostStatusText));
            RefreshTargets();
            RequestAutoSave(debounce: true);
        }
    }

    private int _brightnessBoostDefaultDurationHours;

    public int BrightnessBoostDefaultDurationHours
    {
        get => _brightnessBoostDefaultDurationHours;
        set
        {
            var clamped = Math.Clamp(value, 1, 24);
            if (_brightnessBoostDefaultDurationHours == clamped)
                return;

            _brightnessBoostDefaultDurationHours = clamped;
            _config.Config.BrightnessBoostDefaultDurationHours = clamped;
            OnChanged();
            OnChanged(nameof(BrightnessBoostStatusText));
            RequestAutoSave(debounce: true);
        }
    }

    public string BrightnessBoostStatusText
    {
        get
        {
            var boost = $"{BrightnessBoostPercent}%";
            var duration = _brightnessBoostDefaultDurationHours == 1
                ? "1 hour"
                : $"{_brightnessBoostDefaultDurationHours} hours";

            if (!BrightnessBoostEnabled)
                return $"Brightness boost will add {boost} to brightness for {duration} when enabled.";

            var endUtc = _brightnessBoost.EndTimeUtc;
            var timeText = endUtc.HasValue ? $"Ends at {endUtc.Value.ToLocalTime():HH:mm}." : string.Empty;
            return $"Brightness boost is active (+{boost}). {timeText}";
        }
    }

    private bool _disableMonitorAccessWhileLocked;

    public bool DisableMonitorAccessWhileLocked
    {
        get => _disableMonitorAccessWhileLocked;
        set
        {
            if (_disableMonitorAccessWhileLocked == value)
                return;

            _disableMonitorAccessWhileLocked = value;
            _config.Config.DisableMonitorAccessWhileLocked = value;
            OnChanged();
            RequestAutoSave();
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
            RequestAutoSave();
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
            RequestAutoSave(debounce: true);
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
            RequestAutoSave();
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
            RefreshTargets();
            RequestAutoSave(debounce: true);
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
            RequestAutoSave();
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

    public ICommand RefreshCommand { get; }
    public ICommand ResetAllCommand { get; }
    public ICommand ResetCurveCommand { get; }
    public RelayCommand CheckForUpdatesCommand { get; }

    public string StatusText { get; private set; } = string.Empty;
    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);
    public string EmptyStateText { get; private set; } = string.Empty;

    public SettingsWindowViewModel(
        BrightSyncEngine engine,
        AutoBrightnessService autoBrightness,
        IdleReductionService idleReduction,
        EyeProtectionService eyeProtection,
        BrightnessBoostService brightnessBoost,
        ConfigManager config,
        DdcCiService ddc,
        UpdateChecker updateChecker)
    {
        _engine = engine;
        _autoBrightness = autoBrightness;
        _idleReduction = idleReduction;
        _eyeProtection = eyeProtection;
        _brightnessBoost = brightnessBoost;
        _config = config;
        _ddc = ddc;
        _updateChecker = updateChecker;

        var initial = engine.MasterBrightness;
        _internalBrightness = initial >= 0 ? initial : 50;
        _enforcementInterval = config.Config.EnforcementIntervalSeconds;
        _enforcementEnabled = config.Config.EnforcementEnabled;
        _startWithWindows = config.Config.StartWithWindows;
        _useLegacyDdcCiDetection = config.Config.UseLegacyDdcCiDetection;
        _energySaverReductionEnabled = config.Config.EnergySaverReductionEnabled;
        _energySaverReductionPercent = Math.Clamp(config.Config.EnergySaverReductionPercent, 5, 50);
        _eyeProtectionEnabled = config.Config.EyeProtectionEnabled;
        _eyeProtectionReductionPercent = Math.Clamp(config.Config.EyeProtectionReductionPercent, 5, 80);
        _eyeProtectionDefaultDurationHours = Math.Clamp(config.Config.EyeProtectionDefaultDurationHours, 1, 24);
        _brightnessBoostEnabled = config.Config.BrightnessBoostEnabled;
        _brightnessBoostPercent = Math.Clamp(config.Config.BrightnessBoostPercent, 5, 100);
        _brightnessBoostDefaultDurationHours = Math.Clamp(config.Config.BrightnessBoostDefaultDurationHours, 1, 24);
        _disableMonitorAccessWhileLocked = config.Config.DisableMonitorAccessWhileLocked;
        _idleReductionEnabled = config.Config.IdleReductionEnabled;
        _idleTimeoutMinutes = Math.Clamp(config.Config.IdleTimeoutMinutes, 1, 120);
        _idleReductionToMinimum = config.Config.IdleReductionToMinimum;
        _idleReductionPercent = Math.Clamp(config.Config.IdleReductionPercent, 10, 100);
        _idleIgnoreMediaPlayback = config.Config.IdleIgnoreMediaPlayback;

        RefreshCommand = new RelayCommand(Refresh);
        ResetAllCommand = new RelayCommand(ResetAll);
        ResetCurveCommand = new RelayCommand(ResetCurve);
        CheckForUpdatesCommand = new RelayCommand(CheckForUpdates, () => !_isCheckingForUpdates);

        engine.MasterBrightnessChanged += OnInternalBrightnessChanged;
        engine.TargetsChanged += OnTargetsChanged;
        autoBrightness.StateChanged += OnAutoBrightnessChanged;
        idleReduction.StateChanged += OnIdleReductionChanged;
        eyeProtection.StateChanged += OnEyeProtectionChanged;
        brightnessBoost.StateChanged += OnBrightnessBoostChanged;
        BuildMonitorList();
    }

    private void OnEyeProtectionChanged(object? sender, bool e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
        {
            _eyeProtectionEnabled = e;
            OnChanged(nameof(EyeProtectionEnabled));
            OnChanged(nameof(EyeProtectionStatusText));
            RefreshTargets();
        });
    }

    private void OnBrightnessBoostChanged(object? sender, bool e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
        {
            _brightnessBoostEnabled = e;
            OnChanged(nameof(BrightnessBoostEnabled));
            OnChanged(nameof(BrightnessBoostStatusText));
            RefreshTargets();
        });
    }

    private void BuildMonitorList()
    {
        Monitors.Clear();
        foreach (var monitor in _ddc.GetMonitors())
        {
            var profile = _config.GetOrCreateProfile(monitor.DeviceName);
            Monitors.Add(new MonitorRowViewModel(
                monitor,
                profile,
                _engine,
                OnMonitorReset,
                OnMonitorSettingsChanged,
                CollapseOtherMonitorRows));
        }

        OnChanged(nameof(HasMonitors));
        if (Monitors.Count == 0)
            EmptyStateText = "No displays detected. Try Refresh.";
        else
            EmptyStateText = string.Empty;
        OnChanged(nameof(EmptyStateText));
        Log.Information("Settings monitor list rebuilt. MonitorCount={MonitorCount}", Monitors.Count);
    }

    private void CollapseOtherMonitorRows(MonitorRowViewModel expandedMonitor)
    {
        foreach (var monitor in Monitors)
        {
            if (!ReferenceEquals(monitor, expandedMonitor))
                monitor.IsExpanded = false;
        }
    }

    private void OnInternalBrightnessChanged(object? sender, int brightness)
    {
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
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

    private void SaveCore(string statusText, string logMessage)
    {
        _autoSaveDebounce?.Dispose();
        _autoSaveDebounce = null;
        _config.Save();
        _idleReduction.ReevaluateNow();
        _engine.ForceSync();
        _autoBrightness.RecalculateNow();
        SetStatus(statusText);
        Log.Information("{LogMessage}", logMessage);
    }

    private void RequestAutoSave(bool debounce = false, string? statusText = null)
    {
        if (_suspendAutoSave)
            return;

        if (!debounce)
        {
            SaveCore(statusText ?? $"Saved automatically at {DateTime.Now:HH:mm:ss}", "Settings auto-saved from UI");
            return;
        }

        _autoSaveDebounce?.Dispose();
        _autoSaveDebounce = new System.Threading.Timer(
            _ =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
                {
                    SaveCore($"Saved automatically at {DateTime.Now:HH:mm:ss}",
                        "Settings auto-saved from UI after debounce");
                });
            }, null, 600, System.Threading.Timeout.Infinite);
    }

    private void RunWithAutoSaveSuspended(Action action)
    {
        _suspendAutoSave = true;
        try
        {
            action();
        }
        finally
        {
            _suspendAutoSave = false;
        }
    }

    private void Refresh()
    {
        Log.Information("Settings UI requested monitor refresh");
        SetStatus("Refreshing monitors...");
        Task.Run(() =>
        {
            _engine.RefreshMonitors();
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() => { RefreshMonitorList("Found {0} monitor(s)"); });
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
        RunWithAutoSaveSuspended(() =>
        {
            _config.Config.Monitors.Clear();
            _config.Config.AutoBrightness = AutoBrightnessSettings.CreateDefault();
            _config.Config.MasterBrightness = 50;
            EnforcementIntervalSeconds = new AppConfig().EnforcementIntervalSeconds;
            EnforcementEnabled = true;
            DisableMonitorAccessWhileLocked = false;
            IdleReductionEnabled = false;
            IdleTimeoutMinutes = new AppConfig().IdleTimeoutMinutes;
            IdleReductionToMinimum = false;
            IdleReductionPercent = new AppConfig().IdleReductionPercent;
            IdleIgnoreMediaPlayback = true;
            EnergySaverReductionEnabled = true;
            EnergySaverReductionPercent = 10;
            EyeProtectionEnabled = false;
            EyeProtectionReductionPercent = 20;
            EyeProtectionDefaultDurationHours = 3;
            BrightnessBoostEnabled = false;
            BrightnessBoostPercent = 20;
            BrightnessBoostDefaultDurationHours = 1;
            StartWithWindows = false;
            UseLegacyDdcCiDetection = false;

            foreach (var monitor in Monitors)
                monitor.Reset();
        });

        OnChanged(nameof(AutoBrightnessEnabled));
        OnChanged(nameof(AutoBrightnessLockEnabled));
        OnChanged(nameof(IsManualBrightnessEnabled));
        OnChanged(nameof(ManualBrightnessDisabledReasonText));
        OnChanged(nameof(AutoBrightnessStatusText));
        OnChanged(nameof(AutoBrightnessLockDescription));
        OnChanged(nameof(AutoBrightnessCurvePoints));
        OnChanged(nameof(AutoBrightnessPreviewText));
        OnChanged(nameof(IdleReductionStatusText));
        OnChanged(nameof(IsIdleReductionPercentVisible));
        OnChanged(nameof(IdleTimeoutText));
        OnChanged(nameof(IdleReductionPercentText));
        OnChanged(nameof(EyeProtectionEnabled));
        OnChanged(nameof(EyeProtectionStatusText));
        OnChanged(nameof(BrightnessBoostEnabled));
        OnChanged(nameof(BrightnessBoostStatusText));
        AutoBrightnessCurveChanged?.Invoke(this, EventArgs.Empty);
        SaveCore("Reset all settings to defaults and saved.",
            "All settings were reset to defaults and auto-saved from the UI");
        Log.Warning("All settings were reset to defaults in the UI");
    }

    public void UpdateAutoBrightnessPoint(int index, int brightness, bool isDragging = false)
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

        if (!isDragging)
        {
            _autoBrightness.RecalculateNow();
            foreach (var monitor in Monitors)
                monitor.RefreshTargetText();
            RequestAutoSave(debounce: true);
        }
    }

    private void ResetCurve()
    {
        RunWithAutoSaveSuspended(() =>
        {
            var currentEnabled = _config.Config.AutoBrightness.Enabled;
            var currentLockEnabled = _config.Config.AutoBrightness.LockWhenManualBrightnessChanges;
            _config.Config.AutoBrightness = AutoBrightnessSettings.CreateDefault();
            _config.Config.AutoBrightness.Enabled = currentEnabled;
            _config.Config.AutoBrightness.LockWhenManualBrightnessChanges = currentLockEnabled;
        });

        OnChanged(nameof(AutoBrightnessEnabled));
        OnChanged(nameof(AutoBrightnessLockEnabled));
        OnChanged(nameof(IsManualBrightnessEnabled));
        OnChanged(nameof(ManualBrightnessDisabledReasonText));
        OnChanged(nameof(AutoBrightnessStatusText));
        OnChanged(nameof(AutoBrightnessLockDescription));
        OnChanged(nameof(AutoBrightnessCurvePoints));
        OnChanged(nameof(AutoBrightnessPreviewText));
        AutoBrightnessCurveChanged?.Invoke(this, EventArgs.Empty);
        _autoBrightness.RecalculateNow();
        foreach (var monitor in Monitors)
            monitor.RefreshTargetText();

        SaveCore("Automatic brightness curve reset and saved.",
            "Automatic brightness curve reset and auto-saved from the UI");
        Log.Information("Automatic brightness curve reset in the UI");
    }

    public IReadOnlyList<string> GetCurveHourLabels()
    {
        return ["0", "3", "6", "9", "12", "15", "18", "21", "24"];
    }

    private void OnAutoBrightnessChanged(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
        {
            _isUpdatingInternalBrightness = true;
            InternalBrightness = _autoBrightness.GetCurrentBrightness();
            _isUpdatingInternalBrightness = false;
            OnChanged(nameof(AutoBrightnessEnabled));
            OnChanged(nameof(AutoBrightnessLockEnabled));
            OnChanged(nameof(IsManualBrightnessEnabled));
            OnChanged(nameof(ManualBrightnessDisabledReasonText));
            OnChanged(nameof(AutoBrightnessStatusText));
            OnChanged(nameof(AutoBrightnessLockDescription));
            OnChanged(nameof(AutoBrightnessPreviewText));
            AutoBrightnessCurveChanged?.Invoke(this, EventArgs.Empty);
            foreach (var monitor in Monitors)
                monitor.RefreshTargetText();
        });
    }

    private void OnTargetsChanged(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
        {
            RefreshTargets();
            OnChanged(nameof(IdleReductionStatusText));
        });
    }

    private void OnIdleReductionChanged(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
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
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
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
        if (_suspendAutoSave)
            return;

        SaveCore("Monitor settings reset and saved.", "Monitor settings reset and auto-saved from the UI");
    }

    private void OnMonitorSettingsChanged(bool debounce)
    {
        RequestAutoSave(debounce);
    }

    /// <summary>Sets the status text and auto-clears it after 10 seconds.</summary>
    private void SetStatus(string text)
    {
        StatusText = text;
        OnChanged(nameof(StatusText));
        OnChanged(nameof(HasStatusText));

        _statusTimer?.Dispose();
        _statusTimer = new System.Threading.Timer(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
            {
                StatusText = string.Empty;
                OnChanged(nameof(StatusText));
                OnChanged(nameof(HasStatusText));
            });
        }, null, 5_000, System.Threading.Timeout.Infinite);
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _brightnessDebounce?.Dispose();
        _autoSaveDebounce?.Dispose();
        _statusTimer?.Dispose();
        _engine.MasterBrightnessChanged -= OnInternalBrightnessChanged;
        _engine.TargetsChanged -= OnTargetsChanged;
        _autoBrightness.StateChanged -= OnAutoBrightnessChanged;
        _idleReduction.StateChanged -= OnIdleReductionChanged;
        Log.Debug("Disposed settings window view model");
    }
}
