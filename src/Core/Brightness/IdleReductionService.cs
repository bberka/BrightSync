using BrightSync.Core.Config;
using Microsoft.Win32;
using Serilog;
using Timer = System.Threading.Timer;

namespace BrightSync.Core.Brightness;

public sealed class IdleReductionService : IDisposable
{
    public event EventHandler? StateChanged;

    private readonly BrightSyncEngine _engine;
    private readonly ConfigManager _config;
    private readonly Timer _timer;
    private bool _disposed;

    public IdleReductionService(BrightSyncEngine engine, ConfigManager config)
    {
        _engine = engine;
        _config = config;
        _timer = new Timer(_ => SafeEvaluate(), null, Timeout.Infinite, Timeout.Infinite);
    }

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
    }

    private TimeSpan GetIdleDuration()
    {
        var info = new LASTINPUTINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        var currentTick = unchecked((uint)Environment.TickCount64);
        var idleMilliseconds = unchecked(currentTick - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }

    private bool IsMediaPlaying()
    {
        // Media-session probing through ad-hoc COM interop proved unstable here and could
        // crash the process with an AccessViolationException on some systems. Keep idle
        // dimming safe and simply skip playback suppression until a more robust backend is added.
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}