using BrightSync.Core.Config;
using Serilog;
using Timer = System.Threading.Timer;

namespace BrightSync.Core.Brightness;

public sealed class EyeProtectionService : IDisposable
{
    public event EventHandler<bool>? StateChanged;

    private readonly IBrightnessEngineOperations _engine;
    private readonly ConfigManager _config;
    private readonly Timer _timer;
    private readonly Func<DateTime> _utcNow;
    private BrightnessBoostService? _brightnessBoost;
    private bool _disposed;

    public bool IsEnabled => _config.Config.EyeProtectionEnabled;
    public DateTime? EndTimeUtc => _config.Config.EyeProtectionEndUtc;

    public EyeProtectionService(BrightSyncEngine engine, ConfigManager config)
        : this(new BrightnessEngineOperations(engine), config)
    {
    }

    internal EyeProtectionService(
        IBrightnessEngineOperations engine,
        ConfigManager config,
        Func<DateTime>? utcNow = null)
    {
        _engine = engine;
        _config = config;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _timer = new Timer(_ => CheckExpiry(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SetBrightnessBoostService(BrightnessBoostService brightnessBoost)
    {
        _brightnessBoost = brightnessBoost;
    }

    public void Start()
    {
        if (IsEnabled)
        {
            if (EndTimeUtc.HasValue && EndTimeUtc.Value <= _utcNow())
            {
                Log.Information("Eye protection mode expired during startup");
                SetEnabled(false);
            }
            else
            {
                Log.Information("Eye protection mode restored from config. Ends at {EndUtc}", EndTimeUtc);
                _timer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            }
        }
    }

    public void SetEnabled(bool enabled, int? durationHours = null)
    {
        if (enabled)
        {
            var hours = durationHours ?? _config.Config.EyeProtectionDefaultDurationHours;
            if (!TimedModeDuration.TryCalculateEndUtc(hours, _utcNow(), out var endTimeUtc))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(durationHours),
                    hours,
                    $"Eye protection duration must be between {TimedModeDuration.MinimumHours} and " +
                    $"{TimedModeDuration.MaximumHours} hours and produce a representable UTC expiry.");
            }

            if (_brightnessBoost?.IsEnabled == true)
            {
                Log.Information("Disabling brightness boost because eye protection was enabled");
                _brightnessBoost.SetEnabled(false);
            }

            var previousEnabled = _config.Config.EyeProtectionEnabled;
            var previousEndTimeUtc = _config.Config.EyeProtectionEndUtc;
            _config.Config.EyeProtectionEnabled = true;
            _config.Config.EyeProtectionEndUtc = endTimeUtc;
            try
            {
                _config.Save();
            }
            catch
            {
                _config.Config.EyeProtectionEnabled = previousEnabled;
                _config.Config.EyeProtectionEndUtc = previousEndTimeUtc;
                throw;
            }

            _timer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            Log.Information("Eye protection enabled for {Hours} hours. Ends at {EndUtc}", hours,
                endTimeUtc);
        }
        else
        {
            var previousEnabled = _config.Config.EyeProtectionEnabled;
            var previousEndTimeUtc = _config.Config.EyeProtectionEndUtc;
            _config.Config.EyeProtectionEnabled = false;
            _config.Config.EyeProtectionEndUtc = null;
            try
            {
                _config.Save();
            }
            catch
            {
                _config.Config.EyeProtectionEnabled = previousEnabled;
                _config.Config.EyeProtectionEndUtc = previousEndTimeUtc;
                throw;
            }

            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            Log.Information("Eye protection disabled");
        }

        _engine.ForceSync();
        StateChanged?.Invoke(this, enabled);
    }

    private void CheckExpiry()
    {
        if (IsEnabled && EndTimeUtc.HasValue && EndTimeUtc.Value <= _utcNow())
        {
            Log.Information("Eye protection mode expired");
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
