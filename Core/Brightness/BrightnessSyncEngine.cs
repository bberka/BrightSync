using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using Microsoft.Win32;
using Serilog;

namespace BrightSync.Core.Brightness;

/// <summary>
/// Core sync engine.
/// - Listens to internal display brightness changes via <see cref="InternalBrightnessWatcher"/>.
/// - Applies normalised brightness to all enabled DDC/CI monitors.
/// - Re-enforces brightness on a timer to recover from monitor power cycles.
/// - Re-syncs on system resume from sleep/hibernate.
/// </summary>
public sealed class BrightSyncEngine : IDisposable
{
    public event EventHandler<int>? InternalBrightnessChanged;

    private readonly DdcCiService _ddc;
    private readonly InternalBrightnessWatcher _watcher;
    private readonly ConfigManager _config;
    private readonly System.Timers.Timer _enforcementTimer;
    private int _lastInternalBrightness = -1;
    private bool _isSessionLocked;
    private bool _disposed;

    public int LastInternalBrightness => _lastInternalBrightness;
    public bool IsMonitorAccessSuspended => _config.Config.DisableMonitorAccessWhileLocked && _isSessionLocked;

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

    public void Start()
    {
        _watcher.BrightnessChanged += OnInternalBrightnessChanged;

        // Capture current brightness immediately so external monitors sync on startup
        var current = _watcher.ReadCurrentBrightness();
        if (current >= 0)
        {
            _lastInternalBrightness = current;
            Log.Information("Initial internal brightness detected at {Brightness}%", current);
            SyncAllMonitors();
        }
        else
        {
            Log.Warning("No internal brightness source detected during startup; virtual brightness fallback may be used");
        }

        _watcher.Start();
        _enforcementTimer.Start();
        Log.Debug("Brightness watcher and enforcement timer started. IntervalSeconds={IntervalSeconds}",
            Math.Max(5, _config.Config.EnforcementIntervalSeconds));

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>
    /// Recalculates target brightness for <paramref name="monitorDeviceName"/> using
    /// current internal brightness and the monitor's profile settings.
    /// </summary>
    public int CalculateTarget(string monitorDeviceName, MonitorProfile profile)
    {
        if (_lastInternalBrightness < 0) return profile.MinBrightness;
        var raw = _lastInternalBrightness * profile.Multiplier;
        return (int)Math.Round(Math.Clamp(raw, profile.MinBrightness, profile.MaxBrightness));
    }

    /// <summary>Forces an immediate re-sync of all monitors (e.g. after settings change).</summary>
    public void ForceSync()
    {
        if (_lastInternalBrightness >= 0)
        {
            Log.Debug("Force sync requested at internal brightness {Brightness}%", _lastInternalBrightness);
            SyncAllMonitors();
        }
        else
        {
            Log.Debug("Force sync skipped because no internal brightness value is available");
        }
    }

    /// <summary>Call after monitor list or config changes to rebuild DDC handles.</summary>
    public void RefreshMonitors()
    {
        if (IsMonitorAccessSuspended)
        {
            Log.Information("Monitor refresh skipped because monitor access is paused while the session is locked");
            return;
        }

        Log.Information("Refreshing monitor list");
        _ddc.Refresh();
        Log.Information("Monitor refresh complete. KnownMonitors={MonitorCount}", _ddc.GetMonitors().Count);
        ForceSync();
    }

    /// <summary>
    /// Sets the Windows internal brightness, which then drives external monitor sync.
    /// On desktops without an internal display, still updates the virtual brightness
    /// so external monitors can sync to the slider value.
    /// </summary>
    public bool TrySetInternalBrightness(int brightness)
    {
        return ApplyBrightness(brightness, allowManualWhenAutoEnabled: true, source: "internal");
    }

    public bool TrySetUserBrightness(int brightness)
    {
        if (_config.Config.AutoBrightness.Enabled)
        {
            Log.Debug("Manual brightness request ignored because auto brightness is enabled");
            return false;
        }

        return ApplyBrightness(brightness, allowManualWhenAutoEnabled: false, source: "manual");
    }

    public bool ApplyAutomaticBrightness(int brightness)
    {
        return ApplyBrightness(brightness, allowManualWhenAutoEnabled: true, source: "auto");
    }

    // --- Private ---

    private void OnInternalBrightnessChanged(object? sender, int brightness)
    {
        _lastInternalBrightness = brightness;
        Log.Debug("Internal brightness changed to {Brightness}%", brightness);
        InternalBrightnessChanged?.Invoke(this, brightness);
        Task.Run(() => SyncAllMonitors());
    }

    private bool ApplyBrightness(int brightness, bool allowManualWhenAutoEnabled, string source)
    {
        if (!allowManualWhenAutoEnabled && _config.Config.AutoBrightness.Enabled)
        {
            Log.Debug("Brightness request from {Source} ignored because auto brightness is enabled", source);
            return false;
        }

        var result = _watcher.TrySetBrightness(brightness);
        if (!result)
        {
            _lastInternalBrightness = brightness;
            Log.Warning("Brightness source {Source} could not set internal brightness through WMI; using virtual brightness fallback at {Brightness}%",
                source, brightness);
            InternalBrightnessChanged?.Invoke(this, brightness);
            Task.Run(SyncAllMonitors);
        }
        else
        {
            Log.Debug("Brightness source {Source} requested update to {Brightness}%", source, brightness);
        }

        return result;
    }

    private void SyncAllMonitors()
    {
        if (IsMonitorAccessSuspended)
        {
            Log.Debug("Monitor sync skipped because monitor access is paused while the session is locked");
            return;
        }

        var appliedCount = 0;
        var skippedCount = 0;

        foreach (var monitor in _ddc.GetMonitors())
        {
            if (!monitor.SupportsDdcCi)
            {
                skippedCount++;
                continue;
            }
            var profile = _config.GetOrCreateProfile(monitor.DeviceName);
            if (!profile.Enabled)
            {
                skippedCount++;
                continue;
            }

            var target = CalculateTarget(monitor.DeviceName, profile);
            if (_ddc.SetBrightness(monitor, target))
            {
                appliedCount++;
            }
            else
            {
                Log.Warning("Failed to set brightness for monitor {Monitor}", monitor.FriendlyName);
            }
        }

        Log.Debug("Monitor sync finished. Applied={AppliedCount}, Skipped={SkippedCount}", appliedCount, skippedCount);
    }

    private void Enforce()
    {
        if (IsMonitorAccessSuspended)
        {
            Log.Debug("Brightness enforcement skipped because monitor access is paused while the session is locked");
            return;
        }

        if (!_config.Config.EnforcementEnabled) return;

        // Re-apply without recalculating — just re-send the last commanded value.
        // This recovers monitors that were power-cycled or had their brightness reset.
        var reappliedCount = 0;
        foreach (var monitor in _ddc.GetMonitors())
        {
            if (!monitor.SupportsDdcCi) continue;
            var profile = _config.GetOrCreateProfile(monitor.DeviceName);
            if (!profile.Enabled) continue;
            if (monitor.LastCommandedPercent < 0) continue;

            // Verify actual value matches what we last set; if not, re-apply.
            if (_ddc.TryGetBrightness(monitor, out var actual) &&
                Math.Abs(actual - monitor.LastCommandedPercent) > 1)
            {
                _ddc.SetBrightness(monitor, monitor.LastCommandedPercent);
                reappliedCount++;
            }
        }

        if (reappliedCount > 0)
        {
            Log.Information("Brightness enforcement reapplied values to {MonitorCount} monitor(s)", reappliedCount);
        }
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
        _watcher.BrightnessChanged -= OnInternalBrightnessChanged;
        _enforcementTimer.Stop();
        _enforcementTimer.Dispose();
        _watcher.Dispose();
    }
}
