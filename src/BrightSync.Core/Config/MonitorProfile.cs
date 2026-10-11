namespace BrightSync.Core.Config;

public sealed class MonitorProfile
{
    public const bool DefaultEnabled = true;
    public const int DefaultMinBrightness = 0;
    public const int DefaultMaxBrightness = 100;
    public const double DefaultMultiplier = 1.0;

    /// <summary>Legacy field kept for backward compatibility; monitor names are runtime-detected.</summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>Whether to sync this monitor at all.</summary>
    public bool Enabled { get; set; } = DefaultEnabled;

    /// <summary>
    /// Minimum brightness % to ever set on this monitor (0–100).
    /// Prevents the monitor from going completely dark.
    /// </summary>
    public int MinBrightness { get; set; } = DefaultMinBrightness;

    /// <summary>
    /// Maximum brightness % to ever set on this monitor (0–100).
    /// Useful for very bright monitors that should stay dimmer.
    /// </summary>
    public int MaxBrightness { get; set; } = DefaultMaxBrightness;

    /// <summary>
    /// Scaling factor applied to the internal brightness before clamping.
    /// 1.0 = match internal; 1.2 = 20% brighter; 0.8 = 20% dimmer.
    /// </summary>
    public double Multiplier { get; set; } = DefaultMultiplier;

    public int? Contrast { get; set; }
    public int? ColorPreset { get; set; }
    public int? Volume { get; set; }
    public int? RedGain { get; set; }
    public int? GreenGain { get; set; }
    public int? BlueGain { get; set; }
    public int? InputSource { get; set; }
    public int? RefreshRate { get; set; }
    public int? Sharpness { get; set; }
    public int? Saturation { get; set; }
    public int? Gamma { get; set; }
    public string? AssociatedColorProfile { get; set; }
    public int? PowerState { get; set; }
    public bool AdvancedFeaturesEnabled { get; set; }
    public bool ShowCustomVcpConsole { get; set; }
    public System.Collections.Generic.List<CustomVcpActionProfile> CustomActions { get; set; } = new();

    public void Reset()
    {
        Enabled = DefaultEnabled;
        MinBrightness = DefaultMinBrightness;
        MaxBrightness = DefaultMaxBrightness;
        Multiplier = DefaultMultiplier;
        Contrast = null;
        ColorPreset = null;
        Volume = null;
        RedGain = null;
        GreenGain = null;
        BlueGain = null;
        InputSource = null;
        RefreshRate = null;
        Sharpness = null;
        Saturation = null;
        Gamma = null;
        AssociatedColorProfile = null;
        PowerState = null;
        AdvancedFeaturesEnabled = false;
        ShowCustomVcpConsole = false;
        CustomActions.Clear();
    }
}

public sealed class CustomVcpActionProfile
{
    public string Name { get; set; } = string.Empty;
    public byte VcpCode { get; set; }
    public uint Value { get; set; }
}