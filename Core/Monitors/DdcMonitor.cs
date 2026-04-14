namespace BrightSync.Core.Monitors;

/// <summary>
/// Represents a DDC/CI-capable external monitor.
/// </summary>
public sealed class DdcMonitor
{
    /// <summary>Stable identifier — Windows device name, e.g. \\.\DISPLAY2</summary>
    public string DeviceName { get; init; } = string.Empty;
    /// <summary>Manufacturer name, e.g. "Samsung".</summary>
    public string ManufacturerName { get; init; } = string.Empty;
    /// <summary>Model name without duplicated brand, e.g. "Odyssey G4".</summary>
    public string ModelName { get; init; } = string.Empty;
    /// <summary>Friendly name resolved from WMI (brand + model), e.g. "LG 27GP950-B".</summary>
    public string FriendlyName { get; init; } = string.Empty;
    /// <summary>Raw firmware description from the DDC/CI physical monitor struct.</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>Horizontal resolution in pixels.</summary>
    public int ResolutionWidth { get; init; }
    /// <summary>Vertical resolution in pixels.</summary>
    public int ResolutionHeight { get; init; }
    /// <summary>Refresh rate in Hz, rounded to nearest whole number when available.</summary>
    public int RefreshRateHz { get; init; }
    /// <summary>Display connection type such as HDMI, DisplayPort, or eDP.</summary>
    public string ConnectionType { get; init; } = string.Empty;
    /// <summary>Whether Windows reports this as an internal panel.</summary>
    public bool IsInternal { get; init; }
    /// <summary>Whether the monitor responded to a DDC/CI brightness query.</summary>
    public bool SupportsDdcCi { get; init; }
    /// <summary>Maximum DDC/CI brightness value (usually 100, but can vary).</summary>
    public int MaxDdcBrightness { get; init; } = 100;
    /// <summary>Last brightness value we commanded, in percent (0–100). -1 = unknown.</summary>
    public int LastCommandedPercent { get; set; } = -1;

    internal IntPtr Handle { get; init; }
    // Owning group — needed for DestroyPhysicalMonitors cleanup
    internal PhysicalMonitorGroup? Group { get; init; }
}
