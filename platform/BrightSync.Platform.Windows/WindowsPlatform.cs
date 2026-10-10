using BrightSync.Core.Monitors;
using BrightSync.Core.Services;
using BrightSync.Platform;

namespace BrightSync;

/// <summary>Composition root for the Windows platform services.</summary>
public static class WindowsPlatform
{
    public static PlatformServices Create()
    {
        var internalBrightness = new WindowsInternalBrightness();
        return new PlatformServices
        {
            Capabilities = new PlatformCapabilities
            {
                OsName = "Windows",
                StartupLabel = "Start with Windows",
                EnergySaverLabel = "Windows Energy Saver",
                DisplaySettingsLabel = "Windows Display settings",
                CanSetRefreshRate = true,
                CanManageColorProfiles = true,
                CanOpenDisplaySettings = true,
                ReportsHdrState = true,
                DetectsEnergySaver = true,
                DetectsMediaPlayback = true,
                DetectsIdleTime = true,
                DetectsSessionLock = true,
                CanSelfInstallUpdates = true,
                TrayShowsNotifications = true
            },
            Monitors = new WindowsMonitorBackend(internalBrightness),
            InternalBrightness = internalBrightness,
            Events = new WindowsSystemEvents(),
            Idle = new WindowsIdleTimeSource(),
            Media = new WindowsMediaPlaybackSource(),
            AutoStart = new WindowsAutoStartManager(),
            DisplaySettings = new WindowsDisplaySettings(),
            ColorProfiles = new WindowsColorProfiles(),
            Shell = new WindowsShellIntegration(),
            CreateEnergySaverSource = () => new WindowsEnergySaverSource(),
            CreateSingleInstanceGuard = () => new WindowsSingleInstanceGuard(),
            CreateNativeTrayIcon = () => new WindowsTrayIcon(),
            UpdateInstaller = new WindowsUpdateInstaller()
        };
    }
}

internal sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    public string AssetExtension => ".exe";
    public string AssetOsToken => "win";
}
