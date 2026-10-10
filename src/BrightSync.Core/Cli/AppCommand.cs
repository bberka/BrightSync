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
    Status,
    AppExit
}

public sealed class AppCommand
{
    public AppCommand(AppCommandType commandType, int? brightnessValue = null, int? stepValue = null,
        int? durationHours = null, bool jsonOutput = false)
    {
        CommandType = commandType;
        BrightnessValue = brightnessValue;
        StepValue = stepValue;
        DurationHours = durationHours;
        JsonOutput = jsonOutput;
    }

    public AppCommandType CommandType { get; }
    public int? BrightnessValue { get; }
    public int? StepValue { get; }
    public int? DurationHours { get; }
    public bool JsonOutput { get; }

    public bool IsOneShotCapable =>
        CommandType is AppCommandType.BrightnessSet or AppCommandType.BrightnessUp or AppCommandType.BrightnessDown;

    public bool RequiresResidentApp => !IsOneShotCapable;
}
