namespace BrightSync.Core.Monitors;

internal enum MonitorBrightnessBackend
{
    None = 0,
    LowLevelDdcCi = 1,
    HighLevelApi = 2,
    WriteOnlyDdcCi = 3,
    InternalWmi = 4
}