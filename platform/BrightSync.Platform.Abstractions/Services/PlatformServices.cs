namespace BrightSync.Platform;

/// <summary>
/// Bundle of operating-system services chosen once at startup by the composition root.
/// Core services read <see cref="Current"/> by default; tests substitute their own bundle.
/// </summary>
public sealed class PlatformServices
{
    private static PlatformServices? _current;

    public required PlatformCapabilities Capabilities { get; init; }
    public required IMonitorBackend Monitors { get; init; }
    public required IInternalBrightness InternalBrightness { get; init; }
    public required ISystemEvents Events { get; init; }
    public required IIdleTimeSource Idle { get; init; }
    public required IMediaPlaybackSource Media { get; init; }
    public required IAutoStartManager AutoStart { get; init; }
    public required IDisplaySettingsService DisplaySettings { get; init; }
    public required IColorProfileService ColorProfiles { get; init; }
    public required IShellIntegration Shell { get; init; }
    public required Func<IEnergySaverSource> CreateEnergySaverSource { get; init; }
    public required Func<ISingleInstanceGuard> CreateSingleInstanceGuard { get; init; }

    /// <summary>Native tray implementation, or null when the app should use Avalonia's cross-platform tray.</summary>
    public Func<ITrayIcon>? CreateNativeTrayIcon { get; init; }

    public IUpdateInstaller? UpdateInstaller { get; init; }

    public static PlatformServices Current
    {
        get => _current ?? throw new InvalidOperationException(
            "PlatformServices.Current was not set. Set it from the composition root before using core services.");
        set => _current = value;
    }

    public static bool IsConfigured => _current is not null;
}
