using BrightSync.Cli;

namespace BrightSync.Tests;

public sealed class CliParserTests
{
    [Fact]
    public void Parse_returns_no_command_when_args_are_empty()
    {
        var result = CliParser.Parse([]);

        Assert.False(result.IsCliInvocation);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Parse_returns_autostart_when_only_autostart_flag_is_present()
    {
        var result = CliParser.Parse(["--autostart"]);

        Assert.False(result.IsCliInvocation);
        Assert.True(result.IsSuccess);
        Assert.True(result.IsAutoStart);
    }

    [Fact]
    public void Parse_parses_brightness_set_command()
    {
        var result = CliParser.Parse(["brightness", "set", "42"]);

        Assert.True(result.IsCliInvocation);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Command);
        Assert.Equal(AppCommandType.BrightnessSet, result.Command!.CommandType);
        Assert.Equal(42, result.Command.BrightnessValue);
    }

    [Fact]
    public void Parse_parses_brightness_up_command()
    {
        var result = CliParser.Parse(["brightness", "up", "10"]);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Command);
        Assert.Equal(AppCommandType.BrightnessUp, result.Command!.CommandType);
        Assert.Equal(10, result.Command.StepValue);
    }

    [Fact]
    public void Parse_parses_eye_protection_hours_argument()
    {
        var result = CliParser.Parse(["eye-protection", "on", "--hours", "3"]);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Command);
        Assert.Equal(AppCommandType.EyeProtectionOn, result.Command!.CommandType);
        Assert.Equal(3, result.Command.DurationHours);
    }

    [Fact]
    public void Parse_rejects_out_of_range_brightness_set_value()
    {
        var result = CliParser.Parse(["brightness", "set", "101"]);

        Assert.False(result.IsSuccess);
        Assert.Equal("Brightness set expects a value from 0 to 100.", result.ErrorMessage);
    }

    [Fact]
    public void Parse_rejects_unknown_command()
    {
        var result = CliParser.Parse(["nonsense"]);

        Assert.False(result.IsSuccess);
        Assert.Equal("Unknown command 'nonsense'.", result.ErrorMessage);
    }
}
