namespace BrightSync.Platform.Linux;

/// <summary>Which desktop services this session offers, decided from the D-Bus names that exist.</summary>
internal readonly record struct LinuxDesktopFeatures(
    bool DetectsEnergySaver,
    bool DetectsMediaPlayback,
    bool DetectsIdleTime,
    bool DetectsSessionLock)
{
    private static readonly string[] PowerProfileNames =
        ["net.hadess.PowerProfiles", "org.freedesktop.UPower.PowerProfiles"];

    private static readonly string[] IdleNames =
        ["org.gnome.Mutter.IdleMonitor", "org.freedesktop.ScreenSaver"];

    private static readonly string[] SessionLockNames =
        ["org.gnome.ScreenSaver", "org.freedesktop.ScreenSaver"];

    /// <param name="systemNames">Running and activatable names on the system bus (empty when unreachable).</param>
    /// <param name="sessionNames">Running and activatable names on the session bus (empty when unreachable).</param>
    /// <param name="x11IdleAvailable">Whether the XScreenSaver fallback can run.</param>
    public static LinuxDesktopFeatures Detect(
        IReadOnlyCollection<string> systemNames,
        IReadOnlyCollection<string> sessionNames,
        bool x11IdleAvailable)
        => new(
            DetectsEnergySaver: PowerProfileNames.Any(systemNames.Contains),
            DetectsMediaPlayback: sessionNames.Count > 0,
            DetectsIdleTime: IdleNames.Any(sessionNames.Contains) || x11IdleAvailable,
            DetectsSessionLock: systemNames.Contains("org.freedesktop.login1") ||
                                SessionLockNames.Any(sessionNames.Contains));
}
