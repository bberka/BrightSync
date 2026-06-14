using System.Globalization;

namespace BrightSync.Cli;

public static class CliParser
{
    public static CliParseResult Parse(string[] args)
    {
        if (args.Length == 0)
            return CliParseResult.NoCommand();

        if (args.Length == 1 && IsMatch(args[0], "--autostart"))
            return CliParseResult.AutoStart();

        return args[0].ToLowerInvariant() switch
        {
            "brightness" => ParseBrightness(args),
            "settings" => ParseSettings(args),
            "monitors" => ParseMonitors(args),
            "auto" => ParseAuto(args),
            "eye-protection" => ParseTimedToggle(args, AppCommandType.EyeProtectionOn,
                AppCommandType.EyeProtectionOff, "eye-protection"),
            "boost" => ParseTimedToggle(args, AppCommandType.BoostOn, AppCommandType.BoostOff, "boost"),
            "app" => ParseApp(args),
            _ => CliParseResult.Invalid($"Unknown command '{args[0]}'.")
        };
    }

    private static CliParseResult ParseBrightness(string[] args)
    {
        if (args.Length != 3)
            return CliParseResult.Invalid("Usage: brightness set <0-100> | brightness up <1-100> | brightness down <1-100>.");

        if (!TryParseInt(args[2], out var value))
            return CliParseResult.Invalid($"Brightness value '{args[2]}' is not a valid integer.");

        return args[1].ToLowerInvariant() switch
        {
            "set" when value is >= 0 and <= 100 => CliParseResult.Success(
                new AppCommand(AppCommandType.BrightnessSet, brightnessValue: value)),
            "up" when value is >= 1 and <= 100 => CliParseResult.Success(
                new AppCommand(AppCommandType.BrightnessUp, stepValue: value)),
            "down" when value is >= 1 and <= 100 => CliParseResult.Success(
                new AppCommand(AppCommandType.BrightnessDown, stepValue: value)),
            "set" => CliParseResult.Invalid("Brightness set expects a value from 0 to 100."),
            "up" or "down" => CliParseResult.Invalid("Brightness up/down expects a step from 1 to 100."),
            _ => CliParseResult.Invalid("Usage: brightness set <0-100> | brightness up <1-100> | brightness down <1-100>.")
        };
    }

    private static CliParseResult ParseSettings(string[] args)
        => args.Length == 2 && IsMatch(args[1], "show")
            ? CliParseResult.Success(new AppCommand(AppCommandType.SettingsShow))
            : CliParseResult.Invalid("Usage: settings show.");

    private static CliParseResult ParseMonitors(string[] args)
        => args.Length == 2 && IsMatch(args[1], "refresh")
            ? CliParseResult.Success(new AppCommand(AppCommandType.MonitorsRefresh))
            : CliParseResult.Invalid("Usage: monitors refresh.");

    private static CliParseResult ParseAuto(string[] args)
        => args.Length == 2
            ? args[1].ToLowerInvariant() switch
            {
                "on" => CliParseResult.Success(new AppCommand(AppCommandType.AutoOn)),
                "off" => CliParseResult.Success(new AppCommand(AppCommandType.AutoOff)),
                _ => CliParseResult.Invalid("Usage: auto on | auto off.")
            }
            : CliParseResult.Invalid("Usage: auto on | auto off.");

    private static CliParseResult ParseTimedToggle(string[] args, AppCommandType onType, AppCommandType offType,
        string commandName)
    {
        if (args.Length == 2)
        {
            return args[1].ToLowerInvariant() switch
            {
                "on" => CliParseResult.Success(new AppCommand(onType)),
                "off" => CliParseResult.Success(new AppCommand(offType)),
                _ => CliParseResult.Invalid($"Usage: {commandName} on|off [--hours N].")
            };
        }

        if (args.Length == 4 && IsMatch(args[1], "on") && IsMatch(args[2], "--hours"))
        {
            if (!TryParseInt(args[3], out var hours) || hours <= 0)
                return CliParseResult.Invalid($"'{args[3]}' is not a valid positive hour count.");

            return CliParseResult.Success(new AppCommand(onType, durationHours: hours));
        }

        return CliParseResult.Invalid($"Usage: {commandName} on|off [--hours N].");
    }

    private static CliParseResult ParseApp(string[] args)
        => args.Length == 2 && IsMatch(args[1], "exit")
            ? CliParseResult.Success(new AppCommand(AppCommandType.AppExit))
            : CliParseResult.Invalid("Usage: app exit.");

    private static bool TryParseInt(string value, out int parsed)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);

    private static bool IsMatch(string value, string expected)
        => value.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
