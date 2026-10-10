using BrightSync.Platform;

namespace BrightSync;

/// <summary>Selects the platform services compiled into this build. Exactly one platform project is referenced.</summary>
internal static class PlatformBootstrap
{
    public static PlatformServices Create()
    {
#if PLATFORM_WINDOWS
        return WindowsPlatform.Create();
#elif PLATFORM_LINUX
        return LinuxPlatform.Create();
#else
#error Unsupported target platform. Build with a win-* or linux-* RuntimeIdentifier.
#endif
    }
}
