namespace BrightSync.Cli;

public enum ResidentCommandDispatchStatus
{
    NotRunning,
    Success,
    Failed
}

public sealed class ResidentCommandDispatchResult(ResidentCommandDispatchStatus status, CliExecutionResult? result)
{
    public ResidentCommandDispatchStatus Status { get; } = status;
    public CliExecutionResult? Result { get; } = result;

    public static ResidentCommandDispatchResult NotRunning()
        => new(ResidentCommandDispatchStatus.NotRunning, result: null);

    public static ResidentCommandDispatchResult Success(CliExecutionResult result)
        => new(ResidentCommandDispatchStatus.Success, result);

    public static ResidentCommandDispatchResult Failed(CliExecutionResult result)
        => new(ResidentCommandDispatchStatus.Failed, result);
}
