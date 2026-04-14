namespace BrightSync.Core.Config;

public sealed class AppConfig
{
    /// <summary>Per-monitor settings keyed by Windows device name (e.g. \\.\DISPLAY2).</summary>
    public Dictionary<string, MonitorProfile> Monitors { get; set; } = new();
    /// <summary>How often (seconds) the engine re-applies brightness to catch drift.</summary>
    public int EnforcementIntervalSeconds { get; set; } = 10;
    /// <summary>Whether periodic brightness enforcement is enabled.</summary>
    public bool EnforcementEnabled { get; set; } = true;
    /// <summary>Whether to launch BrightSync when the user logs in.</summary>
    public bool StartWithWindows { get; set; }
    /// <summary>The last local calendar date when BrightSync checked for updates.</summary>
    public DateOnly? LastUpdateCheckDate { get; set; }
}