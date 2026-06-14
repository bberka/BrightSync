namespace BrightSync.Cli;

public sealed class CommandResponse
{
    public bool Success { get; init; }
    public CliExitCode ExitCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public int? AppliedBrightness { get; init; }

    public CliExecutionResult ToExecutionResult()
        => Success
            ? CliExecutionResult.Success(Message)
            : CliExecutionResult.Failure(ExitCode, Message);

    public static CommandResponse Ok(string message, int? appliedBrightness = null)
        => new()
        {
            Success = true,
            ExitCode = CliExitCode.Success,
            Message = message,
            AppliedBrightness = appliedBrightness
        };

    public static CommandResponse Error(CliExitCode exitCode, string message)
        => new()
        {
            Success = false,
            ExitCode = exitCode,
            Message = message
        };
}
