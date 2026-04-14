namespace BrightSync.Core.Config;

public sealed class MonitorProfile
{
    /// <summary>Legacy field kept for backward compatibility; monitor names are runtime-detected.</summary>
    public string FriendlyName { get; set; } = string.Empty;
    /// <summary>Whether to sync this monitor at all.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Minimum brightness % to ever set on this monitor (0–100).
    /// Prevents the monitor from going completely dark.
    /// </summary>
    public int MinBrightness { get; set; } = 0;
    /// <summary>
    /// Maximum brightness % to ever set on this monitor (0–100).
    /// Useful for very bright monitors that should stay dimmer.
    /// </summary>
    public int MaxBrightness { get; set; } = 100;
    /// <summary>
    /// Scaling factor applied to the internal brightness before clamping.
    /// 1.0 = match internal; 1.2 = 20% brighter; 0.8 = 20% dimmer.
    /// </summary>
    public double Multiplier { get; set; } = 1.0;
}