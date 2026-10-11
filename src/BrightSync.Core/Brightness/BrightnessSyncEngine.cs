using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using BrightSync.Platform;
using Serilog;
using Timer = System.Timers.Timer;

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
    private readonly ConfigManager _config;

    private readonly DdcCiService _ddc;
    public DdcCiService Ddc => _ddc;
    private readonly object _timerLock = new();
    private readonly Timer _enforcementTimer;
    private readonly Timer _periodicRefreshTimer;
    private readonly InternalBrightnessWatcher _watcher;
    private readonly ISystemEvents _events = PlatformServices.Current.Events;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private BrightnessBoostService? _brightnessBoost;
    private int _disposed;
    private int _enforcementInProgress;
    private int _periodicRefreshInProgress;
    private int _periodicRefreshRequested;
    private int _syncWorkerActive;
    private int _syncRequested;
    private EyeProtectionService? _eyeProtection;
    private bool _idleReductionActive;
    private bool _isSessionLocked;
    private int _masterBrightness = -1;
    private PowerSavingService? _powerSaving;

    public BrightSyncEngine(
        DdcCiService ddc,
        InternalBrightnessWatcher watcher,
        ConfigManager config)
    {
        _ddc = ddc;
        _watcher = watcher;
        _config = config;

        _enforcementTimer = new Timer(
            Math.Max(5, _config.Config.EnforcementIntervalSeconds) * 1000.0);
        _enforcementTimer.Elapsed += (_, _) => RunEnforcementTimer();

        _periodicRefreshTimer = new Timer();
        _periodicRefreshTimer.Elapsed += (_, _) => RunPeriodicRefreshTimer();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public int MasterBrightness => _masterBrightness;
    public bool IsMonitorAccessSuspended => _config.Config.DisableMonitorAccessWhileLocked && _isSessionLocked;
    public bool IsIdleReductionActive => _config.Config.IdleReductionEnabled && _idleReductionActive;

    public bool IsEnergySaverActive =>
        _config.Config.EnergySaverReductionEnabled && (_powerSaving?.IsEnergySaverActive ?? false);

    public bool IsEyeProtectionActive => _config.Config.EyeProtectionEnabled;
    public bool IsBrightnessBoostActive => _config.Config.BrightnessBoostEnabled;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Log.Debug("Disposing brightness sync engine");
        _lifetimeCts.Cancel();
        _events.Resumed -= OnResumed;
        _events.SessionLockChanged -= OnSessionLockChanged;
        lock (_timerLock)
        {
            _enforcementTimer.Stop();
            _enforcementTimer.Dispose();
            _periodicRefreshTimer.Stop();
            _periodicRefreshTimer.Dispose();
        }
        _watcher.Dispose();
    }

    public event EventHandler<int>? MasterBrightnessChanged;
    public event EventHandler? TargetsChanged;

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
        if (IsDisposed)
            return;

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

        lock (_timerLock)
        {
            if (IsDisposed)
                return;

            _enforcementTimer.Start();
            Log.Debug("Enforcement timer started. IntervalSeconds={IntervalSeconds}",
                Math.Max(5, _config.Config.EnforcementIntervalSeconds));
        }

        UpdatePeriodicRefreshTimer();

        _events.Resumed += OnResumed;
        _events.SessionLockChanged += OnSessionLockChanged;

        // Sync all monitors (including the internal monitor which is now a target) on startup
        if (!_config.Config.AutoBrightness.Enabled)
        {
            SyncAllMonitors();
        }
        else
        {
            Log.Information("Skipping initial monitor sync because auto brightness is enabled");
        }
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

    private void OnResumed(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;

        Log.Information("System resume detected; scheduling monitor refresh");
        // Give displays a moment to initialise after wake
        _ = ScheduleRefreshAfterDelay(TimeSpan.FromSeconds(2), "system resume");
    }

    private void OnSessionLockChanged(object? sender, bool locked)
    {
        if (IsDisposed)
            return;

        if (locked)
        {
            _isSessionLocked = true;
            if (_config.Config.DisableMonitorAccessWhileLocked)
                Log.Information("Session locked; pausing external monitor access");
            return;
        }

        var wasSuspended = IsMonitorAccessSuspended;
        _isSessionLocked = false;

        if (!wasSuspended)
            return;

        Log.Information("Session unlocked; scheduling monitor refresh");
        _ = ScheduleRefreshAfterDelay(TimeSpan.FromMilliseconds(1500), "session unlock");
    }

    internal Task ScheduleRefreshAfterDelay(TimeSpan delay)
        => ScheduleRefreshAfterDelay(delay, "scheduled monitor refresh");

    private async Task ScheduleRefreshAfterDelay(TimeSpan delay, string reason)
    {
        var cancellationToken = _lifetimeCts.Token;
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || IsDisposed)
                return;

            Log.Debug("Running delayed monitor refresh. Reason={Reason}", reason);
            RefreshMonitors();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || IsDisposed)
        {
            // Disposal is the expected cancellation path for delayed work.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Delayed monitor refresh failed. Reason={Reason}", reason);
        }
    }

    public void UpdatePeriodicRefreshTimer()
    {
        lock (_timerLock)
        {
            if (IsDisposed)
                return;

            _periodicRefreshTimer.Stop();

            if (_config.Config.PeriodicMonitorRefreshEnabled)
            {
                var intervalMs = Math.Max(1, _config.Config.PeriodicMonitorRefreshIntervalMinutes) * 60 * 1000.0;
                _periodicRefreshTimer.Interval = intervalMs;
                _periodicRefreshTimer.Start();
                Log.Information("Periodic monitor refresh timer started. IntervalMinutes={IntervalMinutes}",
                    _config.Config.PeriodicMonitorRefreshIntervalMinutes);
            }
            else
            {
                Log.Debug("Periodic monitor refresh timer stopped");
            }
        }
    }

    private void PeriodicRefresh()
    {
        if (IsDisposed)
            return;

        if (IsMonitorAccessSuspended)
        {
            Log.Debug("Periodic monitor refresh skipped because monitor access is paused while the session is locked");
            return;
        }

        Log.Information("Triggering periodic monitor refresh");
        RefreshMonitors();
    }

    private void RunEnforcementTimer()
    {
        if (Interlocked.Exchange(ref _enforcementInProgress, 1) != 0)
            return;

        try
        {
            if (!IsDisposed)
                Enforce();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background brightness enforcement failed");
        }
        finally
        {
            Volatile.Write(ref _enforcementInProgress, 0);
        }
    }

    private void RunPeriodicRefreshTimer()
    {
        if (IsDisposed)
            return;

        Volatile.Write(ref _periodicRefreshRequested, 1);
        if (Interlocked.Exchange(ref _periodicRefreshInProgress, 1) != 0)
            return;

        try
        {
            while (!IsDisposed)
            {
                Volatile.Write(ref _periodicRefreshRequested, 0);
                PeriodicRefresh();

                if (Volatile.Read(ref _periodicRefreshRequested) == 0)
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background periodic monitor refresh failed");
        }
        finally
        {
            Volatile.Write(ref _periodicRefreshInProgress, 0);
            if (!IsDisposed && Volatile.Read(ref _periodicRefreshRequested) != 0)
            {
                _ = Task.Run(RunPeriodicRefreshTimer);
            }
        }
    }
}
