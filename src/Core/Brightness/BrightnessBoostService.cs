using BrightSync.Core.Config;
using Serilog;
using Timer = System.Threading.Timer;

namespace BrightSync.Core.Brightness;

public sealed class BrightnessBoostService : IDisposable
{
    public event EventHandler<bool>? StateChanged;

    private readonly IBrightnessEngineOperations _engine;
    private readonly ConfigManager _config;
    private readonly Timer _timer;
    private readonly Func<DateTime> _utcNow;
    private EyeProtectionService? _eyeProtection;
    private bool _disposed;

    public bool IsEnabled => _config.Config.BrightnessBoostEnabled;
    public DateTime? EndTimeUtc => _config.Config.BrightnessBoostEndUtc;

    public BrightnessBoostService(BrightSyncEngine engine, ConfigManager config)
        : this(new BrightnessEngineOperations(engine), config)
    {
    }

    internal BrightnessBoostService(
        IBrightnessEngineOperations engine,
        ConfigManager config,
        Func<DateTime>? utcNow = null)
    {
        _engine = engine;
        _config = config;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _timer = new Timer(_ => CheckExpiry(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SetEyeProtectionService(EyeProtectionService eyeProtection)
    {
        _eyeProtection = eyeProtection;
    }

    public void Start()
    {
        if (IsEnabled)
        {
            if (EndTimeUtc.HasValue && EndTimeUtc.Value <= _utcNow())
            {
                Log.Information("Brightness boost mode expired during startup");
                SetEnabled(false);
            }
            else
            {
                Log.Information("Brightness boost mode restored from config. Ends at {EndUtc}", EndTimeUtc);
                _timer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            }
        }
    }

    public void SetEnabled(bool enabled, int? durationHours = null)
    {
        if (enabled)
        {
            var hours = durationHours ?? _config.Config.BrightnessBoostDefaultDurationHours;
            if (!TimedModeDuration.TryCalculateEndUtc(hours, _utcNow(), out var endTimeUtc))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(durationHours),
                    hours,
                    $"Brightness boost duration must be between {TimedModeDuration.MinimumHours} and " +
                    $"{TimedModeDuration.MaximumHours} hours and produce a representable UTC expiry.");
            }

            if (_eyeProtection?.IsEnabled == true)
            {
                Log.Information("Disabling eye protection because brightness boost was enabled");
                _eyeProtection.SetEnabled(false);
            }

            var previousEnabled = _config.Config.BrightnessBoostEnabled;
            var previousEndTimeUtc = _config.Config.BrightnessBoostEndUtc;
            _config.Config.BrightnessBoostEnabled = true;
            _config.Config.BrightnessBoostEndUtc = endTimeUtc;
            try
            {
                _config.Save();
            }
            catch
            {
                _config.Config.BrightnessBoostEnabled = previousEnabled;
                _config.Config.BrightnessBoostEndUtc = previousEndTimeUtc;
                throw;
            }

            _timer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            Log.Information("Brightness boost enabled for {Hours} hours. Ends at {EndUtc}", hours,
                endTimeUtc);
        }
        else
        {
            var previousEnabled = _config.Config.BrightnessBoostEnabled;
            var previousEndTimeUtc = _config.Config.BrightnessBoostEndUtc;
            _config.Config.BrightnessBoostEnabled = false;
            _config.Config.BrightnessBoostEndUtc = null;
            try
            {
                _config.Save();
            }
            catch
            {
                _config.Config.BrightnessBoostEnabled = previousEnabled;
                _config.Config.BrightnessBoostEndUtc = previousEndTimeUtc;
                throw;
            }

            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            Log.Information("Brightness boost disabled");
        }

        _engine.ForceSync();
        StateChanged?.Invoke(this, enabled);
    }

    private void CheckExpiry()
    {
        if (IsEnabled && EndTimeUtc.HasValue && EndTimeUtc.Value <= _utcNow())
        {
            Log.Information("Brightness boost mode expired");
            Avalonia.Threading.Dispatcher.UIThread.Invoke(() => SetEnabled(false));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
