namespace BrightSync.Cli;

public sealed class CliExecutionResult(CliExitCode exitCode, string message, bool isError)
{
    public CliExitCode ExitCode { get; } = exitCode;
    public string Message { get; } = message;
    public bool IsError { get; } = isError;

    public static CliExecutionResult Success(string message)
        => new(CliExitCode.Success, message, isError: false);

    public static CliExecutionResult Failure(CliExitCode exitCode, string message)
        => new(exitCode, message, isError: true);
}
