using System.Collections.Generic;

namespace BrightSync.Core.Monitors;

/// <summary>
/// Represents a connected display that BrightSync can inspect and, in some cases, control.
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

    /// <summary>Whether BrightSync can control this display's brightness through any supported backend.</summary>
    public bool SupportsDdcCi { get; init; }

    /// <summary>Whether the current backend can read back brightness as well as write it.</summary>
    public bool SupportsBrightnessRead { get; init; }

    /// <summary>Native minimum brightness reported by the backend.</summary>
    public int MinNativeBrightness { get; init; }

    /// <summary>Native maximum brightness value reported by the backend (usually 100, but can vary).</summary>
    public int MaxDdcBrightness { get; init; } = 100;

    /// <summary>Last brightness value we commanded, in percent (0–100). -1 = unknown.</summary>
    public int LastCommandedPercent { get; set; } = -1;

    /// <summary>Typed backend used for external brightness control.</summary>
    internal MonitorBrightnessBackend BrightnessBackendType { get; init; }

    /// <summary>Backend used for external brightness control, e.g. DDC/CI or high-level API.</summary>
    public string BrightnessBackend { get; init; } = string.Empty;

    /// <summary>Whether HDR is supported on this display according to DisplayConfig.</summary>
    public bool IsHdrSupported { get; init; }

    /// <summary>Whether HDR is currently enabled on this display according to DisplayConfig.</summary>
    public bool IsHdrEnabled { get; init; }

    /// <summary>Current SDR white level in nits when Windows reports it.</summary>
    public int SdrWhiteLevelNits { get; init; }

    /// <summary>Whether this display was identified as an Apple display.</summary>
    public bool IsAppleDisplay { get; init; }

    /// <summary>Whether this display was identified specifically as an Apple Studio Display.</summary>
    public bool IsAppleStudioDisplay { get; init; }

    /// <summary>Which metadata path produced the visible monitor identity and connection info.</summary>
    public string DetectionBackend { get; init; } = string.Empty;

    /// <summary>Human-readable diagnostics describing the detection decisions and fallbacks.</summary>
    public string DetectionDetails { get; init; } = string.Empty;

    public List<uint>? SupportedPresets { get; set; }
    public List<uint>? SupportedInputs { get; set; }

    public bool SupportsContrast { get; set; }
    public int MaxContrast { get; set; } = 100;
    public int CurrentContrast { get; set; }

    public bool SupportsVolume { get; set; }
    public int MaxVolume { get; set; } = 100;
    public int CurrentVolume { get; set; }

    public bool SupportsColorPreset { get; set; }
    public int CurrentColorPreset { get; set; }

    public bool SupportsRgbGains { get; set; }
    public int MaxRgbGain { get; set; } = 100;
    public int CurrentRedGain { get; set; }
    public int CurrentGreenGain { get; set; }
    public int CurrentBlueGain { get; set; }

    public bool SupportsInputSource { get; set; }
    public int CurrentInputSource { get; set; }

    internal IntPtr Handle { get; init; }

    // Owning group — needed for DestroyPhysicalMonitors cleanup
    internal PhysicalMonitorGroup? Group { get; init; }
}