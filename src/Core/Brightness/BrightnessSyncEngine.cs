using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using Microsoft.Win32;
using Serilog;

namespace BrightSync.Core.Brightness;

/// <summary>
/// Core sync engine.
/// - Manages the master brightness state.
/// - Calculates and applies target brightness to all monitors (including internal WMI panels).
/// - Re-enforces brightness on a timer to recover from monitor power cycles.
/// - Re-syncs on system resume from sleep/hibernate.
/// </summary>
public sealed partial class BrightSyncEngine : IDisposable
{
    public event EventHandler<int>? MasterBrightnessChanged;
    public event EventHandler? TargetsChanged;

    private readonly DdcCiService _ddc;
    private readonly InternalBrightnessWatcher _watcher;
    private readonly ConfigManager _config;
    private PowerSavingService? _powerSaving;
    private EyeProtectionService? _eyeProtection;
    private BrightnessBoostService? _brightnessBoost;
    private readonly System.Timers.Timer _enforcementTimer;
    private int _masterBrightness = -1;
    private bool _isSessionLocked;
    private bool _idleReductionActive;
    private bool _disposed;

    public int MasterBrightness => _masterBrightness;
    public bool IsMonitorAccessSuspended => _config.Config.DisableMonitorAccessWhileLocked && _isSessionLocked;
    public bool IsIdleReductionActive => _config.Config.IdleReductionEnabled && _idleReductionActive;

    public bool IsEnergySaverActive =>
        _config.Config.EnergySaverReductionEnabled && (_powerSaving?.IsEnergySaverActive ?? false);

    public bool IsEyeProtectionActive => _config.Config.EyeProtectionEnabled;
    public bool IsBrightnessBoostActive => _config.Config.BrightnessBoostEnabled;

    public BrightSyncEngine(
        DdcCiService ddc,
        InternalBrightnessWatcher watcher,
        ConfigManager config)
    {
        _ddc = ddc;
        _watcher = watcher;
        _config = config;

        _enforcementTimer = new System.Timers.Timer(
            Math.Max(5, _config.Config.EnforcementIntervalSeconds) * 1000.0);
        _enforcementTimer.Elapsed += (_, _) => Enforce();
    }

    public void SetPowerSavingService(PowerSavingService powerSaving)
    {
        _powerSaving = powerSaving;
    }

    public void SetEyeProtectionService(EyeProtectionService eyeProtection)
    {
        _eyeProtection = eyeProtection;
    }

    public void SetBrightnessBoostService(BrightnessBoostService brightnessBoost)
    {
        _brightnessBoost = brightnessBoost;
    }

    public void Start()
    {
        // Capture initial brightness from config, or fallback to internal display's current brightness, or 50.
        var initial = _config.Config.MasterBrightness;
        if (initial == -1)
        {
            var current = _watcher.ReadCurrentBrightness();
            initial = current >= 0 ? current : 50;
            _config.Config.MasterBrightness = initial;
            _config.Save();
        }

        _masterBrightness = Math.Clamp(initial, 0, 100);
        Log.Information("Initial master brightness set to {Brightness}%", _masterBrightness);

        _enforcementTimer.Start();
        Log.Debug("Enforcement timer started. IntervalSeconds={IntervalSeconds}",
            Math.Max(5, _config.Config.EnforcementIntervalSeconds));

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        // Sync all monitors (including the internal monitor which is now a target) on startup
        SyncAllMonitors();
    }

    /// <summary>
    /// Recalculates target brightness for <paramref name="monitorDeviceName"/> using
    /// current master brightness and the monitor's profile settings.
    /// </summary>
    public int CalculateTarget(string monitorDeviceName, MonitorProfile profile)
    {
        if (_masterBrightness < 0) return profile.MinBrightness;
        var raw = _masterBrightness * profile.Multiplier;
        var target = (int)Math.Round(Math.Clamp(raw, profile.MinBrightness, profile.MaxBrightness));

        if (IsEnergySaverActive)
        {
            var energySaverScale = Math.Clamp(100 - _config.Config.EnergySaverReductionPercent, 50, 100) / 100.0;
            target = (int)Math.Round(
                Math.Clamp(target * energySaverScale, profile.MinBrightness, profile.MaxBrightness));
        }

        if (IsEyeProtectionActive)
        {
            target = Math.Clamp(
                target - Math.Clamp(_config.Config.EyeProtectionReductionPercent, 5, 80),
                profile.MinBrightness,
                profile.MaxBrightness);
        }

        if (IsBrightnessBoostActive)
        {
            target = Math.Clamp(
                target + Math.Clamp(_config.Config.BrightnessBoostPercent, 5, 100),
                profile.MinBrightness,
                profile.MaxBrightness);
        }

        if (!IsIdleReductionActive)
            return target;

        if (_config.Config.IdleReductionToMinimum)
            return profile.MinBrightness;

        var scale = Math.Clamp(_config.Config.IdleReductionPercent, 10, 100) / 100.0;
        return (int)Math.Round(Math.Clamp(target * scale, profile.MinBrightness, profile.MaxBrightness));
    }

    private void RaiseTargetsChanged()
    {
        TargetsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Log.Information("System resume detected; scheduling monitor refresh");
            // Give displays a moment to initialise after wake
            Task.Delay(2000).ContinueWith(_ => RefreshMonitors());
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                _isSessionLocked = true;
                if (_config.Config.DisableMonitorAccessWhileLocked)
                    Log.Information("Windows session locked; pausing external monitor access");
                break;

            case SessionSwitchReason.SessionUnlock:
                var wasSuspended = IsMonitorAccessSuspended;
                _isSessionLocked = false;

                if (!wasSuspended)
                    return;

                Log.Information("Windows session unlocked; scheduling monitor refresh");
                Task.Delay(1500).ContinueWith(_ => RefreshMonitors());
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Debug("Disposing brightness sync engine");
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _enforcementTimer.Stop();
        _enforcementTimer.Dispose();
        _watcher.Dispose();
    }
}
