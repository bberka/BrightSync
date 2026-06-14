using System.Text.Json.Serialization;

namespace BrightSync.Cli;

public sealed class CommandServerInfo
{
    public string BaseUrl { get; init; } = string.Empty;
    public string BearerToken { get; init; } = string.Empty;
    public int Pid { get; init; }
    public DateTime StartedUtc { get; init; }
}

[JsonSerializable(typeof(CommandServerInfo))]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResponse))]
internal sealed partial class CliJsonContext : JsonSerializerContext
{
}
