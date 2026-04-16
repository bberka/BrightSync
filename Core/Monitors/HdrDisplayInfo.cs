namespace BrightSync.Core.Monitors;

internal readonly record struct HdrDisplayInfo(
    bool IsHdrSupported,
    bool IsHdrEnabled,
    int SdrWhiteLevelNits)
{
    public static HdrDisplayInfo Empty => new(false, false, 0);
}
