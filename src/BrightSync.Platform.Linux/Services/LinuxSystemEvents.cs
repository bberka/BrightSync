using BrightSync.Platform.Linux.DBus;
using BrightSync.Platform.Linux.Display;
using Serilog;

namespace BrightSync.Platform.Linux.Services;

/// <summary>
/// Linux system notifications: resume and lock from logind / screensavers over D-Bus, wall-clock jumps
/// from a monotonic comparison, and display hot-plug from a DRM sysfs poll. Hooks start on first subscription.
/// </summary>
internal sealed class LinuxSystemEvents(ISysfs sysfs) : ISystemEvents, IDisposable
{
    private static readonly TimeSpan DisplayPollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ClockPollInterval = TimeSpan.FromSeconds(5);

    private readonly object _startLock = new();
    private bool _started;
    private Timer? _displayTimer;
    private Timer? _clockTimer;
    private string _displaySignature = string.Empty;
    private readonly ClockJumpDetector _clock = new();

    private EventHandler? _resumed;
    private EventHandler<bool>? _sessionLockChanged;
    private EventHandler? _timeChanged;
    private EventHandler? _displayConfigurationChanged;

    public event EventHandler? Resumed
    {
        add { _resumed += value; EnsureStarted(); }
        remove => _resumed -= value;
    }

    public event EventHandler<bool>? SessionLockChanged
    {
        add { _sessionLockChanged += value; EnsureStarted(); }
        remove => _sessionLockChanged -= value;
    }

    public event EventHandler? TimeChanged
    {
        add { _timeChanged += value; EnsureStarted(); }
        remove => _timeChanged -= value;
    }

    public event EventHandler? DisplayConfigurationChanged
    {
        add { _displayConfigurationChanged += value; EnsureStarted(); }
        remove => _displayConfigurationChanged -= value;
    }

    public void Dispose()
    {
        _displayTimer?.Dispose();
        _clockTimer?.Dispose();
    }

    /// <summary>Compact fingerprint of the connected displays; a change means hot-plug or a mode switch.</summary>
    internal static string ComputeDisplaySignature(IEnumerable<DrmConnector> connectors)
        => string.Join(
            ';',
            connectors.Select(c => $"{c.SysfsName}:{c.Width}x{c.Height}:{c.EdidBytes?.Length ?? 0}:{c.Edid?.StableKey}"));

    private void EnsureStarted()
    {
        lock (_startLock)
        {
            if (_started)
                return;

            _started = true;
        }

        _displaySignature = ComputeDisplaySignature(DrmScanner.Scan(sysfs));
        _displayTimer = new Timer(_ => PollDisplays(), null, DisplayPollInterval, DisplayPollInterval);
        _clockTimer = new Timer(_ => PollClock(), null, ClockPollInterval, ClockPollInterval);
        _ = Task.Run(SubscribeToDBus);
    }

    private void PollDisplays()
    {
        try
        {
            var signature = ComputeDisplaySignature(DrmScanner.Scan(sysfs));
            if (signature == _displaySignature)
                return;

            _displaySignature = signature;
            Log.Information("Display configuration changed (DRM poll)");
            _displayConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "DRM poll failed");
        }
    }

    private void PollClock()
    {
        if (!_clock.Check(DateTime.UtcNow, Environment.TickCount64))
            return;

        Log.Information("System clock change detected");
        _timeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SubscribeToDBus()
    {
        try
        {
            var system = DBusClient.System;
            system.SubscribeBool(
                "org.freedesktop.login1",
                "/org/freedesktop/login1",
                "org.freedesktop.login1.Manager",
                "PrepareForSleep",
                starting =>
                {
                    if (starting)
                        return;

                    Log.Information("System resumed from sleep");
                    _resumed?.Invoke(this, EventArgs.Empty);
                });

            if (system.TryCall(
                    "org.freedesktop.login1",
                    "/org/freedesktop/login1",
                    "org.freedesktop.login1.Manager",
                    "GetSession",
                    "s",
                    static (ref Tmds.DBus.Protocol.MessageWriter w) => w.WriteString("auto"),
                    static reader => reader.ReadObjectPathAsString(),
                    out string? sessionPath) && sessionPath is not null)
            {
                system.Subscribe("org.freedesktop.login1", sessionPath, "org.freedesktop.login1.Session", "Lock",
                    () => RaiseLock(true));
                system.Subscribe("org.freedesktop.login1", sessionPath, "org.freedesktop.login1.Session", "Unlock",
                    () => RaiseLock(false));
            }

            var session = DBusClient.Session;
            session.SubscribeBool(string.Empty, null, "org.gnome.ScreenSaver", "ActiveChanged", RaiseLock);
            session.SubscribeBool(string.Empty, null, "org.freedesktop.ScreenSaver", "ActiveChanged", RaiseLock);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "D-Bus system event subscriptions could not be started; resume and lock events are unavailable");
        }
    }

    private void RaiseLock(bool locked)
    {
        Log.Information("Session {State}", locked ? "locked" : "unlocked");
        _sessionLockChanged?.Invoke(this, locked);
    }
}

/// <summary>Detects wall-clock changes by comparing elapsed wall time against elapsed monotonic time.</summary>
internal sealed class ClockJumpDetector
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(3);

    private bool _initialized;
    private DateTime _lastUtc;
    private long _lastTickMs;

    /// <summary>True when the wall clock moved by more than the tolerance relative to the monotonic clock.</summary>
    public bool Check(DateTime utcNow, long monotonicMilliseconds)
    {
        if (!_initialized)
        {
            _initialized = true;
            _lastUtc = utcNow;
            _lastTickMs = monotonicMilliseconds;
            return false;
        }

        var wall = utcNow - _lastUtc;
        var monotonic = TimeSpan.FromMilliseconds(monotonicMilliseconds - _lastTickMs);
        _lastUtc = utcNow;
        _lastTickMs = monotonicMilliseconds;
        return (wall - monotonic).Duration() > Tolerance;
    }
}
