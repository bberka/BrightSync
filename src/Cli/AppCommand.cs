namespace BrightSync.Cli;

public enum AppCommandType
{
    BrightnessSet,
    BrightnessUp,
    BrightnessDown,
    SettingsShow,
    MonitorsRefresh,
    AutoOn,
    AutoOff,
    EyeProtectionOn,
    EyeProtectionOff,
    BoostOn,
    BoostOff,
    AppExit
}

public sealed class AppCommand
{
    public AppCommand(AppCommandType commandType, int? brightnessValue = null, int? stepValue = null,
        int? durationHours = null)
    {
        CommandType = commandType;
        BrightnessValue = brightnessValue;
        StepValue = stepValue;
        DurationHours = durationHours;
    }

    public AppCommandType CommandType { get; }
    public int? BrightnessValue { get; }
    public int? StepValue { get; }
    public int? DurationHours { get; }

    public bool IsOneShotCapable =>
        CommandType is AppCommandType.BrightnessSet or AppCommandType.BrightnessUp or AppCommandType.BrightnessDown;

    public bool RequiresResidentApp => !IsOneShotCapable;
}
