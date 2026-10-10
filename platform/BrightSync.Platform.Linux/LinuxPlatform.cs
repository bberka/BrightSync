using BrightSync.Platform;
using BrightSync.Platform.Linux;
using BrightSync.Platform.Linux.DBus;
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
        var features = DetectDesktopFeatures();
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
                DetectsEnergySaver = features.DetectsEnergySaver,
                DetectsMediaPlayback = features.DetectsMediaPlayback,
                DetectsIdleTime = features.DetectsIdleTime,
                DetectsSessionLock = features.DetectsSessionLock,
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
            CreateEnergySaverSource = () => features.DetectsEnergySaver
                ? new LinuxEnergySaverSource()
                : new InertEnergySaverSource(),
            CreateSingleInstanceGuard = () => new LinuxSingleInstanceGuard(),
            CreateNativeTrayIcon = null,
            UpdateInstaller = null
        };
    }

    /// <summary>Asks both buses which services exist so the UI only offers features this desktop can deliver.</summary>
    private static LinuxDesktopFeatures DetectDesktopFeatures()
    {
        try
        {
            var system = Task.Run(() => NamesOf(DBusClient.System));
            var session = Task.Run(() => NamesOf(DBusClient.Session));
            Task.WaitAll([system, session], TimeSpan.FromSeconds(5));
            return LinuxDesktopFeatures.Detect(
                system.IsCompletedSuccessfully ? system.Result : [],
                session.IsCompletedSuccessfully ? session.Result : [],
                X11Native.IsIdleQueryAvailable());
        }
        catch (Exception)
        {
            return LinuxDesktopFeatures.Detect([], [], X11Native.IsIdleQueryAvailable());
        }
    }

    private static HashSet<string> NamesOf(DBusClient bus)
    {
        var names = new HashSet<string>(bus.ListNames(), StringComparer.Ordinal);
        names.UnionWith(bus.ListActivatableNames());
        return names;
    }
}
