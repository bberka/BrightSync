using BrightSync.Platform;
using BrightSync.Platform.Linux.Ddc;
using BrightSync.Platform.Linux.Display;
using BrightSync.Platform.Linux.Monitors;
using BrightSync.Platform.Linux.Services;

namespace BrightSync;

/// <summary>Composition root for the Linux platform services.</summary>
public static class LinuxPlatform
{
    public static PlatformServices Create()
    {
        var sysfs = new RealSysfs();
        var backlight = new BacklightBrightness(sysfs, LinuxLogind.SetBacklight);
        return new PlatformServices
        {
            Capabilities = new PlatformCapabilities
            {
                OsName = "Linux",
                StartupLabel = "Start at login",
                EnergySaverLabel = "Power Saver mode",
                DisplaySettingsLabel = "display settings",
                CanSetRefreshRate = false,
                CanManageColorProfiles = false,
                CanOpenDisplaySettings = false,
                ReportsHdrState = false,
                DetectsEnergySaver = true,
                DetectsMediaPlayback = true,
                DetectsIdleTime = true,
                DetectsSessionLock = true,
                CanSelfInstallUpdates = false,
                TrayShowsNotifications = true
            },
            Monitors = new LinuxMonitorBackend(sysfs, backlight, bus => LinuxI2cBus.TryOpen(bus)),
            InternalBrightness = backlight,
            Events = new LinuxSystemEvents(sysfs),
            Idle = new LinuxIdleTimeSource(),
            Media = new LinuxMediaPlaybackSource(),
            AutoStart = new LinuxAutoStartManager(),
            DisplaySettings = new LinuxDisplaySettings(),
            ColorProfiles = new LinuxColorProfiles(),
            Shell = new LinuxShellIntegration(),
            CreateEnergySaverSource = () => new LinuxEnergySaverSource(),
            CreateSingleInstanceGuard = () => new LinuxSingleInstanceGuard(),
            CreateNativeTrayIcon = null,
            UpdateInstaller = null
        };
    }
}
