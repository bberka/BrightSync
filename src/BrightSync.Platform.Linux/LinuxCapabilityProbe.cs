namespace BrightSync.Platform.Linux;

/// <summary>Facts about the login session taken from its environment.</summary>
internal static class LinuxSession
{
    /// <summary>True when the desktop is a Wayland compositor (X11 clients then run through XWayland).</summary>
    public static bool IsWayland
        => IsWaylandSession(
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"));

    internal static bool IsWaylandSession(string? waylandDisplay, string? sessionType)
        => !string.IsNullOrEmpty(waylandDisplay) ||
           string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Which desktop services this session offers, decided from the D-Bus names that exist.</summary>
internal readonly record struct LinuxDesktopFeatures(
    bool DetectsEnergySaver,
    bool DetectsMediaPlayback,
    bool DetectsIdleTime,
    bool DetectsSessionLock)
{
    private static readonly string[] PowerProfileNames =
        ["net.hadess.PowerProfiles", "org.freedesktop.UPower.PowerProfiles"];

    private const string MutterIdleName = "org.gnome.Mutter.IdleMonitor";
    private const string ScreenSaverName = "org.freedesktop.ScreenSaver";

    private static readonly string[] SessionLockNames =
        ["org.gnome.ScreenSaver", "org.freedesktop.ScreenSaver"];

    /// <param name="systemNames">Running and activatable names on the system bus (empty when unreachable).</param>
    /// <param name="sessionNames">Running and activatable names on the session bus (empty when unreachable).</param>
    /// <param name="x11IdleAvailable">Whether the XScreenSaver fallback can run.</param>
    /// <param name="isWayland">
    /// Whether the session is Wayland. There only Mutter reports idle time: KDE's
    /// <c>GetSessionIdleTime</c> answers "not supported" on Wayland, and XScreenSaver sees XWayland input only.
    /// </param>
    public static LinuxDesktopFeatures Detect(
        IReadOnlyCollection<string> systemNames,
        IReadOnlyCollection<string> sessionNames,
        bool x11IdleAvailable,
        bool isWayland = false)
        => new(
            DetectsEnergySaver: PowerProfileNames.Any(systemNames.Contains),
            DetectsMediaPlayback: sessionNames.Count > 0,
            DetectsIdleTime: sessionNames.Contains(MutterIdleName) ||
                             (!isWayland && (sessionNames.Contains(ScreenSaverName) || x11IdleAvailable)),
            DetectsSessionLock: systemNames.Contains("org.freedesktop.login1") ||
                                SessionLockNames.Any(sessionNames.Contains));
}
