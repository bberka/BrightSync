#if PLATFORM_WINDOWS
using BrightSync.Core.Monitors;
using BrightSync.Core.Services;
using BrightSync.Platform;

namespace BrightSync.Tests;

public sealed class WindowsPlatformTests
{
    [Fact]
    public void InternalBrightness_UsesTheLocalRootWmiNamespace()
        => Assert.Equal(@"\\.\root\WMI", WindowsInternalBrightness.WmiNamespace);

    [Fact]
    public void ColorProfileDirectory_ResolvesThroughMscms()
    {
        var profiles = PlatformServices.Current.ColorProfiles.GetInstalledColorProfiles();

        // Windows always ships sRGB and other profiles in the color directory; an empty list means the
        // mscms entry point failed to resolve (the call is wrapped, so only the result reveals it).
        Assert.NotEmpty(profiles);
    }

    [Fact]
    public void NotificationIconPath_ExpandsTheKnownFolderPrefixExplorerStores()
    {
        var programFiles = new Guid("6D809377-6AF0-444B-8957-A3773F02200E");
        string? Resolve(Guid id) => id == programFiles ? @"C:\Program Files" : null;

        // The installed app: without this the promotion never finds its own entry.
        Assert.Equal(
            @"C:\Program Files\BrightSync\BrightSync.exe",
            WindowsTrayIcon.ExpandKnownFolderPath(@"{6D809377-6AF0-444B-8957-A3773F02200E}\BrightSync\BrightSync.exe", Resolve));

        // Plain paths, unknown folders and malformed prefixes are returned untouched.
        Assert.Equal(@"D:\Tools\BrightSync.exe", WindowsTrayIcon.ExpandKnownFolderPath(@"D:\Tools\BrightSync.exe", Resolve));
        Assert.Equal(
            @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\BrightSync\BrightSync.exe",
            WindowsTrayIcon.ExpandKnownFolderPath(@"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\BrightSync\BrightSync.exe", Resolve));
        Assert.Equal(@"{not-a-guid}\BrightSync.exe", WindowsTrayIcon.ExpandKnownFolderPath(@"{not-a-guid}\BrightSync.exe", Resolve));
        Assert.Equal("{6D809377-6AF0-444B-8957-A3773F02200E}", WindowsTrayIcon.ExpandKnownFolderPath("{6D809377-6AF0-444B-8957-A3773F02200E}", Resolve));
    }

    [Fact]
    public void NotificationIconPath_ResolvesRealKnownFolders()
    {
        // FOLDERID_Windows exists for every process bitness.
        var windows = WindowsTrayIcon.ResolveKnownFolder(new Guid("F38BF404-1D43-42F2-9305-67DE0B28FC23"));

        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            windows,
            ignoreCase: true);
        Assert.Null(WindowsTrayIcon.ResolveKnownFolder(Guid.Empty));
    }
}
#endif
