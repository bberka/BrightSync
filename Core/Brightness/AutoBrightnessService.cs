using BrightSync.Core.Config;
using Microsoft.Win32;
using Serilog;
using Timer = System.Threading.Timer;

namespace BrightSync.Core.Brightness;

public sealed class AutoBrightnessService : IDisposable
{
    private static readonly TimeSpan AutoApplyGracePeriod = TimeSpan.FromSeconds(3);

    public event EventHandler? StateChanged;

    private readonly BrightSyncEngine _engine;
    private readonly ConfigManager _config;
    private readonly Timer _timer;
    private bool _disposed;
    private int _lastAppliedBrightness = -1;
    private DateTime _lastAutoApplyUtc = DateTime.MinValue;

    public AutoBrightnessService(BrightSyncEngine engine, ConfigManager config)
    {
        _engine = engine;
        _config = config;
        _timer = new Timer(_ => SafeRecalculate(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsEnabled => _config.Config.AutoBrightness.Enabled;

    public int LastAppliedBrightness => _lastAppliedBrightness >= 0
        ? _lastAppliedBrightness
        : GetCurrentBrightness();

    public void Start()
    {
        _config.Config.AutoBrightness.EnsureDefaults();
        _engine.InternalBrightnessChanged += OnInternalBrightnessChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(30));
        RecalculateNow();
        RaiseStateChanged();
        Log.Information("Auto brightness service started. Enabled={Enabled}", IsEnabled);
    }

    public void SetEnabled(bool enabled)
    {
        if (_config.Config.AutoBrightness.Enabled == enabled)
            return;

        _config.Config.AutoBrightness.Enabled = enabled;
        Log.Information("Auto brightness {State}", enabled ? "enabled" : "disabled");
        if (enabled)
        {
            _lastAppliedBrightness = -1;
            RecalculateNow();
        }
        else
        {
            _lastAutoApplyUtc = DateTime.MinValue;
        }

        RaiseStateChanged();
    }

    public int GetCurrentBrightness()
    {
        _config.Config.AutoBrightness.EnsureDefaults();
        return AutoBrightnessCurveEvaluator.Evaluate(
            _config.Config.AutoBrightness.Curve,
            DateTime.Now.TimeOfDay);
    }

    public void RecalculateNow()
    {
        if (!IsEnabled)
            return;

        var brightness = GetCurrentBrightness();
        if (brightness != _lastAppliedBrightness || _engine.LastInternalBrightness != brightness)
        {
            _lastAutoApplyUtc = DateTime.UtcNow;
            _lastAppliedBrightness = brightness;
            _engine.ApplyAutomaticBrightness(brightness);
            Log.Debug("Auto brightness applied {Brightness}%", brightness);
        }

        RaiseStateChanged();
    }

    private void SafeRecalculate()
    {
        try
        {
            RecalculateNow();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto brightness recalculation failed");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
            return;

        Log.Information("Auto brightness recalculating after resume");
        Task.Delay(1500).ContinueWith(_ => SafeRecalculate());
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        Log.Information("System time change detected; recalculating auto brightness");
        SafeRecalculate();
    }

    private void OnInternalBrightnessChanged(object? sender, int brightness)
    {
        if (!IsEnabled)
            return;

        if (WasRecentlyAppliedByAuto(brightness))
            return;

        Log.Information(
            "Detected external Windows brightness change to {Brightness}% while auto brightness was enabled; disabling auto brightness",
            brightness);
        _config.Config.AutoBrightness.Enabled = false;
        _lastAutoApplyUtc = DateTime.MinValue;
        _lastAppliedBrightness = -1;
        RaiseStateChanged();
    }

    private bool WasRecentlyAppliedByAuto(int brightness)
    {
        if (_lastAppliedBrightness != brightness)
            return false;

        return DateTime.UtcNow - _lastAutoApplyUtc <= AutoApplyGracePeriod;
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _engine.InternalBrightnessChanged -= OnInternalBrightnessChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        _timer.Dispose();
    }
}
