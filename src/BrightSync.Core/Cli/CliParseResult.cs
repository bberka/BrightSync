namespace BrightSync.Cli;

public sealed class CliParseResult
{
    private CliParseResult(bool isCliInvocation, bool isSuccess, bool isAutoStart, AppCommand? command,
        string? errorMessage)
    {
        IsCliInvocation = isCliInvocation;
        IsSuccess = isSuccess;
        IsAutoStart = isAutoStart;
        Command = command;
        ErrorMessage = errorMessage;
    }

    public bool IsCliInvocation { get; }
    public bool IsSuccess { get; }
    public bool IsAutoStart { get; }
    public AppCommand? Command { get; }
    public string? ErrorMessage { get; }

    public static CliParseResult NoCommand()
        => new(isCliInvocation: false, isSuccess: true, isAutoStart: false, command: null, errorMessage: null);

    public static CliParseResult AutoStart()
        => new(isCliInvocation: false, isSuccess: true, isAutoStart: true, command: null, errorMessage: null);

    public static CliParseResult Success(AppCommand command)
        => new(isCliInvocation: true, isSuccess: true, isAutoStart: false, command: command, errorMessage: null);

    public static CliParseResult Invalid(string message)
        => new(isCliInvocation: true, isSuccess: false, isAutoStart: false, command: null, errorMessage: message);
}
