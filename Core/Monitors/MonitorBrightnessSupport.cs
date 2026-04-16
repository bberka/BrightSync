namespace BrightSync.Core.Monitors;

internal sealed record MonitorBrightnessSupport(
    MonitorBrightnessBackend Backend,
    bool SupportsBrightnessControl,
    bool SupportsBrightnessRead,
    uint MinimumNativeBrightness,
    uint MaximumNativeBrightness,
    int CurrentBrightnessPercent,
    string BackendLabel,
    string DetectionDetails)
{
    public static MonitorBrightnessSupport Unsupported(string details)
        => new(
            MonitorBrightnessBackend.None,
            false,
            false,
            0,
            100,
            -1,
            "Unavailable",
            details);
}
