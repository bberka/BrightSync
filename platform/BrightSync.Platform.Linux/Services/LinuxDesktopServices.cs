using System.Globalization;
using System.Text;
using BrightSync.Platform.Linux.DBus;
using Serilog;
using Tmds.DBus.Protocol;

namespace BrightSync.Platform.Linux.Services;

/// <summary>Detects the OS power-saver profile through power-profiles-daemon, polling its ActiveProfile property.</summary>
internal sealed class LinuxEnergySaverSource : IEnergySaverSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly Func<string?> _readProfile;
    private Timer? _timer;

    public LinuxEnergySaverSource()
        : this(ReadActiveProfileFromDBus)
    {
    }

    internal LinuxEnergySaverSource(Func<string?> readProfile)
    {
        _readProfile = readProfile;
    }

    public bool IsActive { get; private set; }

    public event EventHandler<bool>? Changed;

    public void Start()
    {
        Poll();
        _timer = new Timer(_ => Poll(), null, PollInterval, PollInterval);
    }

    public void Dispose() => _timer?.Dispose();

    internal static bool IsPowerSaverProfile(string? profile)
        => string.Equals(profile, "power-saver", StringComparison.Ordinal);

    internal void Poll()
    {
        var active = IsPowerSaverProfile(_readProfile());
        if (active == IsActive)
            return;

        IsActive = active;
        Log.Information("Power Saver profile is now {Status}", active ? "Active" : "Inactive");
        Changed?.Invoke(this, active);
    }

    private static string? ReadActiveProfileFromDBus()
    {
        var system = DBusClient.System;
        return system.GetStringProperty(
                   "net.hadess.PowerProfiles", "/net/hadess/PowerProfiles", "net.hadess.PowerProfiles", "ActiveProfile")
               ?? system.GetStringProperty(
                   "org.freedesktop.UPower.PowerProfiles", "/org/freedesktop/UPower/PowerProfiles",
                   "org.freedesktop.UPower.PowerProfiles", "ActiveProfile");
    }
}

/// <summary>Idle time from Mutter, KDE's ScreenSaver service, or the X11 screensaver extension, in that order.</summary>
internal sealed class LinuxIdleTimeSource : IIdleTimeSource
{
    private int _source; // 0 = unknown, 1 = mutter, 2 = screensaver service, 3 = x11, -1 = none

    public TimeSpan GetIdleTime()
    {
        if (_source != -1)
        {
            if (_source is 0 or 1 && TryMutter(out var idle))
            {
                _source = 1;
                return idle;
            }

            if (_source is 0 or 2 && TryScreenSaver(out idle))
            {
                _source = 2;
                return idle;
            }

            if (_source is 0 or 3 && X11Native.TryGetIdleTime(out idle))
            {
                _source = 3;
                return idle;
            }

            if (_source == 0)
            {
                _source = -1;
                Log.Warning("No idle-time source is available; idle dimming will not trigger");
            }
        }

        return TimeSpan.Zero;
    }

    private static bool TryMutter(out TimeSpan idle)
    {
        idle = TimeSpan.Zero;
        if (!DBusClient.Session.TryCall(
                "org.gnome.Mutter.IdleMonitor",
                "/org/gnome/Mutter/IdleMonitor/Core",
                "org.gnome.Mutter.IdleMonitor",
                "GetIdletime",
                null,
                null,
                static reader => reader.ReadUInt64(),
                out ulong milliseconds))
        {
            return false;
        }

        idle = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private static bool TryScreenSaver(out TimeSpan idle)
    {
        idle = TimeSpan.Zero;
        if (!DBusClient.Session.TryCall(
                "org.freedesktop.ScreenSaver",
                "/ScreenSaver",
                "org.freedesktop.ScreenSaver",
                "GetSessionIdleTime",
                null,
                null,
                static reader => reader.ReadUInt32(),
                out uint milliseconds))
        {
            return false;
        }

        idle = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }
}

/// <summary>Detects active media players through the MPRIS D-Bus interface.</summary>
internal sealed class LinuxMediaPlaybackSource : IMediaPlaybackSource
{
    private const string MprisPrefix = "org.mpris.MediaPlayer2.";

    public bool IsMediaPlaying()
    {
        var session = DBusClient.Session;
        foreach (var name in session.ListNames())
        {
            if (!name.StartsWith(MprisPrefix, StringComparison.Ordinal))
                continue;

            var status = session.GetStringProperty(
                name, "/org/mpris/MediaPlayer2", "org.mpris.MediaPlayer2.Player", "PlaybackStatus");
            if (string.Equals(status, "Playing", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

/// <summary>Writes an XDG autostart entry so BrightSync launches at login.</summary>
internal sealed class LinuxAutoStartManager(string? autostartDirectory = null, string? executablePath = null)
    : IAutoStartManager
{
    private const string FileName = "brightsync.desktop";

    public void Apply(bool enable)
    {
        var directory = autostartDirectory ?? DefaultAutostartDirectory();
        var path = Path.Combine(directory, FileName);

        if (!enable)
        {
            if (File.Exists(path))
                File.Delete(path);
            Log.Information("Removed autostart entry {Path}", path);
            return;
        }

        var exec = executablePath ?? ResolveExecutable();
        if (string.IsNullOrEmpty(exec))
        {
            Log.Warning("Failed to resolve executable path for autostart registration");
            return;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(path, BuildDesktopEntry(exec));
        Log.Information("Configured autostart using {Path}", path);
    }

    internal static string BuildDesktopEntry(string executable)
    {
        var text = new StringBuilder();
        text.Append("[Desktop Entry]\n");
        text.Append("Type=Application\n");
        text.Append("Name=BrightSync\n");
        text.Append("Comment=Keep monitor brightness in sync\n");
        text.Append(CultureInfo.InvariantCulture, $"Exec={QuoteExecArgument(executable)} --autostart\n");
        text.Append("Icon=brightsync\n");
        text.Append("Terminal=false\n");
        text.Append("Categories=Utility;\n");
        text.Append("X-GNOME-Autostart-enabled=true\n");
        return text.ToString();
    }

    /// <summary>Quotes one argument per the Desktop Entry spec: wrap in double quotes, backslash-escape <c>"`$\</c>.</summary>
    internal static string QuoteExecArgument(string value)
    {
        var quoted = new StringBuilder("\"");
        foreach (var character in value)
        {
            if (character is '"' or '`' or '$' or '\\')
                quoted.Append('\\');
            quoted.Append(character);
        }

        return quoted.Append('"').ToString();
    }

    private static string DefaultAutostartDirectory()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(configHome, "autostart");
    }

    /// <summary>AppImages run from a temporary mount, so the stable path is the image itself.</summary>
    private static string ResolveExecutable()
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        return !string.IsNullOrWhiteSpace(appImage) ? appImage : Environment.ProcessPath ?? string.Empty;
    }
}

/// <summary>Exclusive lock file under the runtime directory keeps a second instance from starting.</summary>
internal sealed class LinuxSingleInstanceGuard(string? lockPath = null) : ISingleInstanceGuard
{
    private FileStream? _lock;

    public bool TryAcquire()
    {
        var path = lockPath ?? DefaultLockPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _lock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }

    private static string DefaultLockPath()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime)
            ? Path.Combine(runtime, "brightsync.lock")
            : Path.Combine(Path.GetTempPath(), $"brightsync-{Environment.UserName}.lock");
    }
}

internal sealed class LinuxDisplaySettings : IDisplaySettingsService
{
    public IReadOnlyList<int> GetSupportedRefreshRates(string deviceName) => [];
    public int GetCurrentRefreshRate(string deviceName) => 0;
    public bool SetRefreshRate(string deviceName, int refreshRate) => false;
}

internal sealed class LinuxColorProfiles : IColorProfileService
{
    public IReadOnlyList<string> GetInstalledColorProfiles() => [];
    public string GetActiveColorProfile(string deviceName) => string.Empty;
    public bool SetActiveColorProfile(string deviceName, string profileName) => false;
}

internal static class LinuxLogind
{
    /// <summary>Sets a backlight through logind, which allows the active session to do so without privileges.</summary>
    public static bool SetBacklight(string deviceName, uint rawValue)
        => DBusClient.System.TryCall(
            "org.freedesktop.login1",
            "/org/freedesktop/login1/session/auto",
            "org.freedesktop.login1.Session",
            "SetBrightness",
            "ssu",
            (ref MessageWriter w) =>
            {
                w.WriteString("backlight");
                w.WriteString(deviceName);
                w.WriteUInt32(rawValue);
            });
}
