using BrightSync.Core.Config;
using Serilog;
using Timer = System.Threading.Timer;

namespace BrightSync.Core.Brightness;

public sealed class BrightnessBoostService : IDisposable
{
    public event EventHandler<bool>? StateChanged;

    private readonly BrightSyncEngine _engine;
    private readonly ConfigManager _config;
    private readonly Timer _timer;
    private EyeProtectionService? _eyeProtection;
    private bool _disposed;

    public bool IsEnabled => _config.Config.BrightnessBoostEnabled;
    public DateTime? EndTimeUtc => _config.Config.BrightnessBoostEndUtc;

    public BrightnessBoostService(BrightSyncEngine engine, ConfigManager config)
    {
        _engine = engine;
        _config = config;
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
            if (EndTimeUtc.HasValue && EndTimeUtc.Value <= DateTime.UtcNow)
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
            if (_eyeProtection?.IsEnabled == true)
            {
                Log.Information("Disabling eye protection because brightness boost was enabled");
                _eyeProtection.SetEnabled(false);
            }

            var hours = durationHours ?? _config.Config.BrightnessBoostDefaultDurationHours;
            _config.Config.BrightnessBoostEnabled = true;
            _config.Config.BrightnessBoostEndUtc = DateTime.UtcNow.AddHours(hours);
            _timer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            Log.Information("Brightness boost enabled for {Hours} hours. Ends at {EndUtc}", hours, _config.Config.BrightnessBoostEndUtc);
        }
        else
        {
            _config.Config.BrightnessBoostEnabled = false;
            _config.Config.BrightnessBoostEndUtc = null;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            Log.Information("Brightness boost disabled");
        }

        _config.Save();
        _engine.ForceSync();
        StateChanged?.Invoke(this, enabled);
    }

    private void CheckExpiry()
    {
        if (IsEnabled && EndTimeUtc.HasValue && EndTimeUtc.Value <= DateTime.UtcNow)
        {
            Log.Information("Brightness boost mode expired");
            System.Windows.Application.Current.Dispatcher.Invoke(() => SetEnabled(false));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
