#if PLATFORM_WINDOWS
using BrightSync.Core.Monitors;
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
}
#endif
