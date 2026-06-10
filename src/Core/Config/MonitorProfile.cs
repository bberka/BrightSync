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

    public void Reset()
    {
        Enabled = DefaultEnabled;
        MinBrightness = DefaultMinBrightness;
        MaxBrightness = DefaultMaxBrightness;
        Multiplier = DefaultMultiplier;
    }
}