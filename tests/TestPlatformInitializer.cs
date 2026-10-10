using System.Runtime.CompilerServices;
using BrightSync.Platform;

namespace BrightSync.Tests;

internal static class TestPlatformInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        PlatformServices.Current = PlatformBootstrap.Create();
    }
}
