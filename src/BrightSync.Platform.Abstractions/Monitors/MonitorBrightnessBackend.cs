namespace BrightSync.Core.Monitors;

public enum MonitorBrightnessBackend
{
    None = 0,
    LowLevelDdcCi = 1,
    HighLevelApi = 2,
    WriteOnlyDdcCi = 3,
    InternalPanel = 4
}