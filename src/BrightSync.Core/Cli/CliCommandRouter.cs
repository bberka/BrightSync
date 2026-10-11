using Serilog;

namespace BrightSync.Cli;

public interface IResidentCommandClient
{
    Task<ResidentCommandDispatchResult> TryDispatchAsync(AppCommand command, CancellationToken cancellationToken);
}

public interface IOneShotCommandExecutor
{
    Task<CliExecutionResult> ExecuteAsync(AppCommand command, CancellationToken cancellationToken);
}

public sealed class CliCommandRouter(
    IResidentCommandClient residentCommandClient,
    IOneShotCommandExecutor oneShotCommandExecutor)
{
    public async Task<CliExecutionResult> RouteAsync(AppCommand command, CancellationToken cancellationToken)
    {
        var residentResult = await residentCommandClient.TryDispatchAsync(command, cancellationToken);
        switch (residentResult.Status)
        {
            case ResidentCommandDispatchStatus.Success:
                return residentResult.Result ?? CliExecutionResult.Success("Command completed.");
            case ResidentCommandDispatchStatus.Failed:
                return residentResult.Result ??
                       CliExecutionResult.Failure(CliExitCode.TransportFailure, "Failed to contact the running BrightSync instance.");
            case ResidentCommandDispatchStatus.NotRunning:
                break;
            default:
                Log.Warning("Unhandled resident dispatch status {Status}", residentResult.Status);
                break;
        }

        if (command.RequiresResidentApp)
        {
            return CliExecutionResult.Failure(
                CliExitCode.ResidentAppRequired,
                "BrightSync must already be running for this command.");
        }

        return await oneShotCommandExecutor.ExecuteAsync(command, cancellationToken);
    }
}
