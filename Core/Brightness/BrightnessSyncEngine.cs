using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using Microsoft.Win32;

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
    private bool _disposed;

    public int LastInternalBrightness => _lastInternalBrightness;

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
        int current = _watcher.ReadCurrentBrightness();
        if (current >= 0)
        {
            _lastInternalBrightness = current;
            SyncAllMonitors(current);
        }

        _watcher.Start();
        _enforcementTimer.Start();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>
    /// Recalculates target brightness for <paramref name="monitorDeviceName"/> using
    /// current internal brightness and the monitor's profile settings.
    /// </summary>
    public int CalculateTarget(string monitorDeviceName, MonitorProfile profile)
    {
        if (_lastInternalBrightness < 0) return profile.MinBrightness;
        double raw = _lastInternalBrightness * profile.Multiplier;
        return (int)Math.Round(Math.Clamp(raw, profile.MinBrightness, profile.MaxBrightness));
    }

    /// <summary>Forces an immediate re-sync of all monitors (e.g. after settings change).</summary>
    public void ForceSync()
    {
        if (_lastInternalBrightness >= 0)
            SyncAllMonitors(_lastInternalBrightness);
    }

    /// <summary>Call after monitor list or config changes to rebuild DDC handles.</summary>
    public void RefreshMonitors()
    {
        _ddc.Refresh();
        ForceSync();
    }

    // --- Private ---

    private void OnInternalBrightnessChanged(object? sender, int brightness)
    {
        _lastInternalBrightness = brightness;
        InternalBrightnessChanged?.Invoke(this, brightness);
        Task.Run(() => SyncAllMonitors(brightness));
    }

    private void SyncAllMonitors(int internalBrightness)
    {
        foreach (var monitor in _ddc.GetMonitors())
        {
            if (!monitor.SupportsDdcCi) continue;
            var profile = _config.GetOrCreateProfile(monitor.DeviceName);
            if (!profile.Enabled) continue;

            int target = CalculateTarget(monitor.DeviceName, profile);
            _ddc.SetBrightness(monitor, target);
        }
    }

    private void Enforce()
    {
        // Re-apply without recalculating — just re-send the last commanded value.
        // This recovers monitors that were power-cycled or had their brightness reset.
        foreach (var monitor in _ddc.GetMonitors())
        {
            if (!monitor.SupportsDdcCi) continue;
            var profile = _config.GetOrCreateProfile(monitor.DeviceName);
            if (!profile.Enabled) continue;
            if (monitor.LastCommandedPercent < 0) continue;

            // Verify actual value matches what we last set; if not, re-apply.
            if (_ddc.TryGetBrightness(monitor, out int actual) &&
                Math.Abs(actual - monitor.LastCommandedPercent) > 1)
            {
                _ddc.SetBrightness(monitor, monitor.LastCommandedPercent);
            }
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            // Give displays a moment to initialise after wake
            Task.Delay(2000).ContinueWith(_ =>
            {
                _ddc.Refresh();
                ForceSync();
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _watcher.BrightnessChanged -= OnInternalBrightnessChanged;
        _enforcementTimer.Stop();
        _enforcementTimer.Dispose();
        _watcher.Dispose();
    }
}
