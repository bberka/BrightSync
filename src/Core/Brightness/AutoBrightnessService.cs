using BrightSync.Core.Config;
using Microsoft.Win32;
using Serilog;
using Timer = System.Threading.Timer;

namespace BrightSync.Core.Brightness;

internal interface IBrightnessEngineOperations
{
    event EventHandler<int>? MasterBrightnessChanged;
    event EventHandler? TargetsChanged;

    int MasterBrightness { get; }
    bool IsIdleReductionActive { get; }

    bool ApplyAutomaticBrightness(int brightness);
    bool TrySetUserBrightness(int brightness);
    int CalculateTarget(string monitorDeviceName, MonitorProfile profile);
    void ForceSync();
}

internal sealed class BrightnessEngineOperations(BrightSyncEngine engine) : IBrightnessEngineOperations
{
    public event EventHandler<int>? MasterBrightnessChanged
    {
        add => engine.MasterBrightnessChanged += value;
        remove => engine.MasterBrightnessChanged -= value;
    }

    public event EventHandler? TargetsChanged
    {
        add => engine.TargetsChanged += value;
        remove => engine.TargetsChanged -= value;
    }

    public int MasterBrightness => engine.MasterBrightness;
    public bool IsIdleReductionActive => engine.IsIdleReductionActive;

    public bool ApplyAutomaticBrightness(int brightness)
        => engine.ApplyAutomaticBrightness(brightness);

    public bool TrySetUserBrightness(int brightness)
        => engine.TrySetUserBrightness(brightness);

    public int CalculateTarget(string monitorDeviceName, MonitorProfile profile)
        => engine.CalculateTarget(monitorDeviceName, profile);

    public void ForceSync()
        => engine.ForceSync();
}

internal static class TimedModeDuration
{
    // These limits match the existing settings controls and ConfigManager validation.
    public const int MinimumHours = 1;
    public const int MaximumHours = 24;

    public static bool IsValidHours(int hours)
        => hours is >= MinimumHours and <= MaximumHours;

    public static bool TryCalculateEndUtc(int hours, DateTime nowUtc, out DateTime endUtc)
    {
        endUtc = default;
        if (!IsValidHours(hours) || nowUtc.Kind != DateTimeKind.Utc)
            return false;

        try
        {
            endUtc = nowUtc.AddHours(hours);
            return endUtc > nowUtc && endUtc.Kind == DateTimeKind.Utc;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

public sealed class AutoBrightnessService : IDisposable
{
    public event EventHandler? StateChanged;

    private readonly IBrightnessEngineOperations _engine;
    private readonly ConfigManager _config;
    private readonly Timer _timer;
    private bool _disposed;
    private int _lastAppliedBrightness = -1;

    public AutoBrightnessService(BrightSyncEngine engine, ConfigManager config)
        : this(new BrightnessEngineOperations(engine), config)
    {
    }

    internal AutoBrightnessService(IBrightnessEngineOperations engine, ConfigManager config)
    {
        _engine = engine;
        _config = config;
        _timer = new Timer(_ => SafeRecalculate(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsEnabled => _config.Config.AutoBrightness.Enabled;
    public bool IsLockEnabled => _config.Config.AutoBrightness.LockWhenManualBrightnessChanges;

    public int LastAppliedBrightness => _lastAppliedBrightness >= 0
        ? _lastAppliedBrightness
        : GetCurrentBrightness();

    public void Start()
    {
        _config.Config.AutoBrightness.EnsureDefaults();
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

        var previousEnabled = _config.Config.AutoBrightness.Enabled;
        _config.Config.AutoBrightness.Enabled = enabled;
        try
        {
            _config.Save();
        }
        catch
        {
            _config.Config.AutoBrightness.Enabled = previousEnabled;
            throw;
        }

        Log.Information("Auto brightness {State}", enabled ? "enabled" : "disabled");
        if (enabled)
        {
            _lastAppliedBrightness = -1;
            RecalculateNow();
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

    public bool RecalculateNow()
    {
        if (!IsEnabled)
            return false;

        var brightness = GetCurrentBrightness();
        var applied = false;
        if (brightness != _lastAppliedBrightness || _engine.MasterBrightness != brightness)
        {
            _lastAppliedBrightness = brightness;
            applied = _engine.ApplyAutomaticBrightness(brightness);
            Log.Debug("Auto brightness applied {Brightness}%", brightness);
        }

        RaiseStateChanged();
        return applied;
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

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        _timer.Dispose();
    }
}
