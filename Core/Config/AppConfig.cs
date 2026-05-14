namespace BrightSync.Core.Config;

public sealed class AppConfig
{
    /// <summary>Per-monitor settings keyed by Windows device name (e.g. \\.\DISPLAY2).</summary>
    public Dictionary<string, MonitorProfile> Monitors { get; set; } = new();
    /// <summary>Global automatic brightness settings.</summary>
    public AutoBrightnessSettings AutoBrightness { get; set; } = AutoBrightnessSettings.CreateDefault();
    /// <summary>How often (seconds) the engine re-applies brightness to catch drift.</summary>
    public int EnforcementIntervalSeconds { get; set; } = 10;
    /// <summary>Whether periodic brightness enforcement is enabled.</summary>
    public bool EnforcementEnabled { get; set; } = true;
    /// <summary>Whether external monitor access should pause while the Windows session is locked.</summary>
    public bool DisableMonitorAccessWhileLocked { get; set; }
    /// <summary>Whether BrightSync should dim external monitor targets after the system is idle.</summary>
    public bool IdleReductionEnabled { get; set; }
    /// <summary>Minutes of no input before BrightSync treats the system as idle.</summary>
    public int IdleTimeoutMinutes { get; set; } = 10;
    /// <summary>Whether idle dimming should set targets directly to each monitor profile's minimum.</summary>
    public bool IdleReductionToMinimum { get; set; }
    /// <summary>Percentage of the normal target to use while idle when not reducing directly to minimum.</summary>
    public int IdleReductionPercent { get; set; } = 50;
    /// <summary>Whether active media playback should suppress idle dimming.</summary>
    public bool IdleIgnoreMediaPlayback { get; set; } = true;
    /// <summary>Whether to use the older, simpler DDC/CI monitor detection path for compatibility.</summary>
    public bool UseLegacyDdcCiDetection { get; set; }
    /// <summary>Whether BrightSync should decrease brightness when Windows Energy Saver is enabled.</summary>
    public bool EnergySaverReductionEnabled { get; set; } = true;
    /// <summary>Percentage to decrease brightness by when Energy Saver is active (e.g. 10 for 10% reduction).</summary>
    public int EnergySaverReductionPercent { get; set; } = 10;
    /// <summary>Whether to launch BrightSync when the user logs in.</summary>
    public bool StartWithWindows { get; set; }
    /// <summary>The last local calendar date when BrightSync checked for updates.</summary>
    public DateOnly? LastUpdateCheckDate { get; set; }
}
