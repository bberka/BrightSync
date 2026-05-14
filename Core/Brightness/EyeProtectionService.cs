using BrightSync.Core.Config;
using Serilog;
using Timer = System.Threading.Timer;

namespace BrightSync.Core.Brightness;

public sealed class EyeProtectionService : IDisposable
{
    public event EventHandler<bool>? StateChanged;

    private readonly BrightSyncEngine _engine;
    private readonly ConfigManager _config;
    private readonly Timer _timer;
    private bool _disposed;

    public bool IsEnabled => _config.Config.EyeProtectionEnabled;
    public DateTime? EndTimeUtc => _config.Config.EyeProtectionEndUtc;

    public EyeProtectionService(BrightSyncEngine engine, ConfigManager config)
    {
        _engine = engine;
        _config = config;
        _timer = new Timer(_ => CheckExpiry(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        if (IsEnabled)
        {
            if (EndTimeUtc.HasValue && EndTimeUtc.Value <= DateTime.UtcNow)
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
            _config.Config.EyeProtectionEnabled = true;
            _config.Config.EyeProtectionEndUtc = DateTime.UtcNow.AddHours(hours);
            _timer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            Log.Information("Eye protection enabled for {Hours} hours. Ends at {EndUtc}", hours, _config.Config.EyeProtectionEndUtc);
        }
        else
        {
            _config.Config.EyeProtectionEnabled = false;
            _config.Config.EyeProtectionEndUtc = null;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            Log.Information("Eye protection disabled");
        }

        _config.Save();
        _engine.ForceSync();
        StateChanged?.Invoke(this, enabled);
    }

    private void CheckExpiry()
    {
        if (IsEnabled && EndTimeUtc.HasValue && EndTimeUtc.Value <= DateTime.UtcNow)
        {
            Log.Information("Eye protection mode expired");
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
