using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrightSync.Cli;

public sealed class CommandResponse
{
    public bool Success { get; init; }
    public CliExitCode ExitCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public int? AppliedBrightness { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CliStatusSnapshot? Status { get; init; }

    public CliExecutionResult ToExecutionResult(bool jsonOutput = false)
    {
        if (!Success)
            return CliExecutionResult.Failure(ExitCode, Message);

        if (jsonOutput && Status is not null)
        {
            var json = JsonSerializer.Serialize(Status, CliJsonContext.Default.CliStatusSnapshot);
            return CliExecutionResult.Success(json);
        }

        return CliExecutionResult.Success(Message);
    }

    public static CommandResponse Ok(
        string message,
        int? appliedBrightness = null,
        CliStatusSnapshot? status = null)
        => new()
        {
            Success = true,
            ExitCode = CliExitCode.Success,
            Message = message,
            AppliedBrightness = appliedBrightness,
            Status = status
        };

    public static CommandResponse Error(CliExitCode exitCode, string message)
        => new()
        {
            Success = false,
            ExitCode = exitCode,
            Message = message
        };
}
