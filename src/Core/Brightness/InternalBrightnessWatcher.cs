using BrightSync.Platform;
using Serilog;

namespace BrightSync.Core.Brightness;

/// <summary>
/// Reads and writes the built-in panel brightness through the active platform backend
/// (WMI on Windows, the backlight class on Linux).
/// </summary>
public sealed class InternalBrightnessWatcher : IDisposable
{
    private readonly IInternalBrightness _backend;

    public InternalBrightnessWatcher()
        : this(PlatformServices.Current.InternalBrightness)
    {
    }

    internal InternalBrightnessWatcher(IInternalBrightness backend)
    {
        _backend = backend;
    }

    public void Start()
    {
        Log.Information("Internal brightness helper initialized");
    }

    /// <summary>Current internal brightness 0-100, or -1 when unavailable.</summary>
    public int ReadCurrentBrightness() => _backend.ReadCurrentBrightness();

    /// <summary>Sets the internal display brightness.</summary>
    public bool TrySetBrightness(int brightness) => _backend.TrySetBrightness(Math.Clamp(brightness, 0, 100));

    public void Dispose()
    {
        // No-op for compatibility
    }
}
