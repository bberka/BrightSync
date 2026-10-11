namespace BrightSync.Platform;

/// <summary>Feature flags the UI uses to hide controls an OS cannot honour.</summary>
public sealed record PlatformCapabilities
{
    public required string OsName { get; init; }
    public required string StartupLabel { get; init; }
    public required string EnergySaverLabel { get; init; }
    public required string DisplaySettingsLabel { get; init; }

    /// <summary>Windows-only older monitor enumeration path; the toggle is hidden elsewhere.</summary>
    public bool HasLegacyDetection { get; init; }

    public bool CanSetRefreshRate { get; init; }
    public bool CanManageColorProfiles { get; init; }
    public bool CanOpenDisplaySettings { get; init; }
    public bool ReportsHdrState { get; init; }
    public bool DetectsEnergySaver { get; init; }
    public bool DetectsMediaPlayback { get; init; }
    public bool DetectsIdleTime { get; init; }
    public bool DetectsSessionLock { get; init; }
    public bool CanSelfInstallUpdates { get; init; }
    public bool TrayShowsNotifications { get; init; }
}
