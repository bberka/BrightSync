using System.Runtime.InteropServices;
using BrightSync.Core.Config;
using Microsoft.Win32;
using Serilog;
using Timer = System.Threading.Timer;
using Windows.Media.Control;


namespace BrightSync.Core.Brightness;

public sealed class IdleReductionService : IDisposable
{
    private readonly ConfigManager _config;

    private readonly BrightSyncEngine _engine;
    private readonly Timer _timer;
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
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer.Dispose();
    }

    public event EventHandler? StateChanged;

    public void Start()
    {
        NormalizeConfig();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
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

        var info = new LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        var currentTick = unchecked((uint)Environment.TickCount64);
        var idleMilliseconds = unchecked(currentTick - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }

    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private bool _mediaManagerInitialized;
    private readonly object _mediaLock = new();

    internal Func<bool>? MediaPlaybackOverride { get; set; }

    private bool IsMediaPlaying()
    {
        if (MediaPlaybackOverride != null)
        {
            return MediaPlaybackOverride();
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return false;
        }

        lock (_mediaLock)
        {
            if (!_mediaManagerInitialized)
            {
                _mediaManagerInitialized = true;
                Task.Run(async () =>
                {
                    try
                    {
                        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                        lock (_mediaLock)
                        {
                            _mediaManager = manager;
                        }
                        Log.Information("GlobalSystemMediaTransportControlsSessionManager initialized successfully.");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to request GlobalSystemMediaTransportControlsSessionManager");
                    }
                });
            }
        }

        GlobalSystemMediaTransportControlsSessionManager? managerToUse;
        lock (_mediaLock)
        {
            managerToUse = _mediaManager;
        }

        if (managerToUse == null)
        {
            return false;
        }

        try
        {
            var sessions = managerToUse.GetSessions();
            if (sessions != null)
            {
                foreach (var session in sessions)
                {
                    var playbackInfo = session.GetPlaybackInfo();
                    if (playbackInfo != null && playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        Log.Debug("Active media session is playing.");
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to query active media session playback status");
        }

        return false;
    }

    private void NormalizeConfig()
    {
        _config.Config.IdleTimeoutMinutes = Math.Clamp(_config.Config.IdleTimeoutMinutes, 1, 120);
        _config.Config.IdleReductionPercent = Math.Clamp(_config.Config.IdleReductionPercent, 10, 100);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
            return;

        Log.Information("System resume detected; re-evaluating idle reduction");
        Task.Delay(1500).ContinueWith(_ => SafeEvaluate());
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}