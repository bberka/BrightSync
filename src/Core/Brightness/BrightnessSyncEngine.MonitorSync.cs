using BrightSync.Core.Interop;
using BrightSync.Core.Monitors;
using Serilog;

namespace BrightSync.Core.Brightness;

public sealed partial class BrightSyncEngine
{
    /// <summary>Forces an immediate re-sync of all monitors (e.g. after settings change).</summary>
    public void ForceSync()
    {
        if (_masterBrightness >= 0)
        {
            Log.Debug("Force sync requested at master brightness {Brightness}%", _masterBrightness);
            SyncAllMonitors();
        }
        else
        {
            Log.Debug("Force sync skipped because no master brightness value is available");
        }

        RaiseTargetsChanged();
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
        ApplyAllPersistedSettings();
    }

    public void ApplyPersistedSettings(DdcMonitor monitor)
    {
        if (IsMonitorAccessSuspended || !monitor.SupportsDdcCi) return;

        var profile = _config.GetOrCreateProfile(monitor.DeviceName);
        if (!profile.Enabled) return;

        if (profile.Contrast.HasValue && monitor.SupportsContrast)
        {
            _ddc.SetVcpFeature(monitor, NativeMethods.VCP_CONTRAST, (uint)profile.Contrast.Value);
        }
        if (profile.Volume.HasValue && monitor.SupportsVolume)
        {
            _ddc.SetVcpFeature(monitor, NativeMethods.VCP_VOLUME, (uint)profile.Volume.Value);
        }
        if (profile.ColorPreset.HasValue && monitor.SupportsColorPreset)
        {
            _ddc.SetVcpFeature(monitor, NativeMethods.VCP_COLOR_PRESET, (uint)profile.ColorPreset.Value);
        }
        if (profile.InputSource.HasValue && monitor.SupportsInputSource)
        {
            _ddc.SetVcpFeature(monitor, NativeMethods.VCP_INPUT_SOURCE, (uint)profile.InputSource.Value);
        }
        if (monitor.SupportsRgbGains && profile.RedGain.HasValue && profile.GreenGain.HasValue && profile.BlueGain.HasValue)
        {
            _ddc.SetVcpFeature(monitor, NativeMethods.VCP_RED_GAIN, (uint)profile.RedGain.Value);
            _ddc.SetVcpFeature(monitor, NativeMethods.VCP_GREEN_GAIN, (uint)profile.GreenGain.Value);
            _ddc.SetVcpFeature(monitor, NativeMethods.VCP_BLUE_GAIN, (uint)profile.BlueGain.Value);
        }
    }

    public void ApplyAllPersistedSettings()
    {
        if (IsMonitorAccessSuspended) return;

        foreach (var monitor in _ddc.GetMonitors())
        {
            ApplyPersistedSettings(monitor);
        }
    }

    public bool TrySetUserBrightness(int brightness)
        => TryApplyUserBrightness(brightness, synchronize: false);

    public bool TrySetUserBrightnessSync(int brightness)
        => TryApplyUserBrightness(brightness, synchronize: true);

    public bool ApplyAutomaticBrightness(int brightness)
        => ApplyBrightness(brightness, allowManualWhenAutoEnabled: true, source: "auto", synchronize: false);

    public bool SetIdleReductionActive(bool active)
    {
        if (_idleReductionActive == active)
            return false;

        _idleReductionActive = active;
        Log.Information("Idle brightness reduction {State}", active ? "activated" : "cleared");

        if (_masterBrightness >= 0)
            Task.Run(SyncAllMonitors);

        RaiseTargetsChanged();
        return true;
    }

    private bool TryApplyUserBrightness(int brightness, bool synchronize)
    {
        if (_config.Config.AutoBrightness.Enabled)
        {
            Log.Debug("Manual brightness request ignored because auto brightness is enabled");
            return false;
        }

        return ApplyBrightness(brightness, allowManualWhenAutoEnabled: false, source: "manual", synchronize);
    }

    private bool ApplyBrightness(int brightness, bool allowManualWhenAutoEnabled, string source, bool synchronize)
    {
        if (!allowManualWhenAutoEnabled && _config.Config.AutoBrightness.Enabled)
        {
            Log.Debug("Brightness request from {Source} ignored because auto brightness is enabled", source);
            return false;
        }

        _masterBrightness = Math.Clamp(brightness, 0, 100);
        if (source == "manual")
        {
            _config.Config.MasterBrightness = _masterBrightness;
            _config.Save();
        }

        Log.Debug("Brightness source {Source} requested update to {Brightness}%", source, brightness);
        MasterBrightnessChanged?.Invoke(this, _masterBrightness);
        if (synchronize)
            SyncAllMonitors();
        else
            Task.Run(SyncAllMonitors);
        return true;
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
                appliedCount++;
            else
                Log.Warning("Failed to set brightness for monitor {Monitor}", monitor.FriendlyName);
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

        if (!_config.Config.EnforcementEnabled)
            return;

        var reappliedCount = 0;
        foreach (var monitor in _ddc.GetMonitors())
        {
            if (!monitor.SupportsDdcCi)
                continue;

            var profile = _config.GetOrCreateProfile(monitor.DeviceName);
            if (!profile.Enabled || monitor.LastCommandedPercent < 0)
                continue;

            if (!monitor.SupportsBrightnessRead)
            {
                Log.Debug(
                    "Brightness enforcement skipped readback for monitor {Monitor} because backend {Backend} does not support brightness reads",
                    monitor.FriendlyName,
                    monitor.BrightnessBackend);
                continue;
            }

            if (monitor.IsHdrEnabled)
            {
                Log.Debug("Brightness enforcement skipped readback for monitor {Monitor} because HDR is enabled",
                    monitor.FriendlyName);
                continue;
            }

            if (_ddc.TryGetBrightness(monitor, out var actual) &&
                Math.Abs(actual - monitor.LastCommandedPercent) > 1)
            {
                _ddc.SetBrightness(monitor, monitor.LastCommandedPercent);
                reappliedCount++;
            }
        }

        if (reappliedCount > 0)
            Log.Information("Brightness enforcement reapplied values to {MonitorCount} monitor(s)", reappliedCount);
    }
}
