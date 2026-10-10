using BrightSync.Core.Config;
using BrightSync.Platform;
using Serilog;
using Timer = System.Threading.Timer;


namespace BrightSync.Core.Brightness;

public sealed class IdleReductionService : IDisposable
{
    private readonly ConfigManager _config;

    private readonly BrightSyncEngine _engine;
    private readonly Timer _timer;
    private readonly ISystemEvents _events = PlatformServices.Current.Events;
    private bool _disposed;

    public IdleReductionService(BrightSyncEngine engine, ConfigManager config)
    {
        _engine = engine;
        _config = config;
        _timer = new Timer(_ => SafeEvaluate(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _events.Resumed -= OnResumed;
        _timer.Dispose();
    }

    public event EventHandler? StateChanged;

    public void Start()
    {
        NormalizeConfig();
        _events.Resumed += OnResumed;
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5));
        ReevaluateNow();
        Log.Information("Idle reduction service started. Enabled={Enabled}", _config.Config.IdleReductionEnabled);
    }

    public void ReevaluateNow()
    {
        NormalizeConfig();
        EvaluateNow();
    }

    private void SafeEvaluate()
    {
        try
        {
            EvaluateNow();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Idle reduction evaluation failed");
        }
    }

    private void EvaluateNow()
    {
        var shouldReduce = false;
        if (_config.Config.IdleReductionEnabled)
        {
            var idleFor = GetIdleDuration();
            var threshold = TimeSpan.FromMinutes(Math.Max(1, _config.Config.IdleTimeoutMinutes));
            var mediaPlaying = _config.Config.IdleIgnoreMediaPlayback && IsMediaPlaying();
            shouldReduce = idleFor >= threshold && !mediaPlaying;
        }

        if (_engine.SetIdleReductionActive(shouldReduce))
            RaiseStateChanged();

        if (_config.Config.IdleReductionEnabled)
        {
            if (shouldReduce)
            {
                // We are currently idle (dimmed). Poll every 1 second (1000ms) for responsive return detection.
                _timer.Change(TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1000));
            }
            else
            {
                // We are not idle. Poll less frequently (every 5 seconds).
                _timer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
        }
        else
        {
            _timer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    internal Func<TimeSpan>? IdleDurationOverride { get; set; }

    public TimeSpan GetIdleDuration()
    {
        if (IdleDurationOverride != null)
        {
            return IdleDurationOverride();
        }

        return PlatformServices.Current.Idle.GetIdleTime();
    }

    internal Func<bool>? MediaPlaybackOverride { get; set; }

    private bool IsMediaPlaying()
    {
        if (MediaPlaybackOverride != null)
        {
            return MediaPlaybackOverride();
        }

        return PlatformServices.Current.Media.IsMediaPlaying();
    }

    private void NormalizeConfig()
    {
        _config.Config.IdleTimeoutMinutes = Math.Clamp(_config.Config.IdleTimeoutMinutes, 1, 120);
        _config.Config.IdleReductionPercent = Math.Clamp(_config.Config.IdleReductionPercent, 10, 100);
    }

    private void OnResumed(object? sender, EventArgs e)
    {
        Log.Information("System resume detected; re-evaluating idle reduction");
        Task.Delay(1500).ContinueWith(_ => SafeEvaluate());
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
