using System.Runtime.CompilerServices;
using BrightSync.Platform;

namespace BrightSync.Tests;

internal static class TestPlatformInitializer
{
    /// <summary>Runs the suite against the real services of the OS the tests are built for.</summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
#if PLATFORM_WINDOWS
        PlatformServices.Current = WindowsPlatform.Create();
#else
        PlatformServices.Current = LinuxPlatform.Create();
#endif
    }
}
