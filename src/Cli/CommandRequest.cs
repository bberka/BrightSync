namespace BrightSync.Cli;

public sealed class CommandRequest
{
    public AppCommandType CommandType { get; init; }
    public int? BrightnessValue { get; init; }
    public int? StepValue { get; init; }
    public bool? Enabled { get; init; }
    public int? DurationHours { get; init; }

    public static CommandRequest FromCommand(AppCommand command)
    {
        bool? enabled = command.CommandType switch
        {
            AppCommandType.AutoOn or AppCommandType.EyeProtectionOn or AppCommandType.BoostOn => true,
            AppCommandType.AutoOff or AppCommandType.EyeProtectionOff or AppCommandType.BoostOff => false,
            _ => null
        };

        return new CommandRequest
        {
            CommandType = command.CommandType,
            BrightnessValue = command.BrightnessValue,
            StepValue = command.StepValue,
            Enabled = enabled,
            DurationHours = command.DurationHours
        };
    }
}
