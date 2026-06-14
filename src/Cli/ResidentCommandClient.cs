using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Serilog;

namespace BrightSync.Cli;

public sealed class ResidentCommandClient : IResidentCommandClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _metadataPath;
    private bool _disposed;

    public ResidentCommandClient(HttpClient? httpClient = null, string? metadataPath = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _metadataPath = metadataPath ?? CommandServerPaths.MetadataFilePath;
    }

    public async Task<ResidentCommandDispatchResult> TryDispatchAsync(AppCommand command,
        CancellationToken cancellationToken)
    {
        var serverInfo = TryReadServerInfo(_metadataPath);
        if (serverInfo == null)
            return ResidentCommandDispatchResult.NotRunning();

        var pingResponse = await SendAsync<object>(serverInfo, "/v1/ping", payload: null, cancellationToken);
        if (pingResponse.Status == ResidentSendStatus.NotRunning)
            return ResidentCommandDispatchResult.NotRunning();

        if (pingResponse.Status == ResidentSendStatus.Failed)
            return ResidentCommandDispatchResult.Failed(pingResponse.Result);

        var commandResponse = await SendAsync(serverInfo, "/v1/commands", CommandRequest.FromCommand(command),
            cancellationToken);
        return commandResponse.Status switch
        {
            ResidentSendStatus.Success => ResidentCommandDispatchResult.Success(commandResponse.Result),
            ResidentSendStatus.NotRunning => ResidentCommandDispatchResult.NotRunning(),
            _ => ResidentCommandDispatchResult.Failed(commandResponse.Result)
        };
    }

    internal static CommandServerInfo? TryReadServerInfo(string metadataPath)
    {
        try
        {
            if (!File.Exists(metadataPath))
                return null;

            var text = File.ReadAllText(metadataPath);
            var info = JsonSerializer.Deserialize(text, CliJsonContext.Default.CommandServerInfo);
            if (info == null || string.IsNullOrWhiteSpace(info.BaseUrl) || string.IsNullOrWhiteSpace(info.BearerToken))
            {
                DeleteMetadata(metadataPath);
                return null;
            }

            if (!IsProcessAlive(info.Pid))
            {
                DeleteMetadata(metadataPath);
                return null;
            }

            return info;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read resident command metadata from {MetadataPath}", metadataPath);
            DeleteMetadata(metadataPath);
            return null;
        }
    }

    private async Task<ResidentSendResult> SendAsync<TPayload>(CommandServerInfo serverInfo, string relativePath,
        TPayload? payload, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(serverInfo.BaseUrl), relativePath));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serverInfo.BearerToken);
            if (payload != null)
            {
                var json = JsonSerializer.Serialize(payload, typeof(TPayload), CliJsonContext.Default);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return ResidentSendResult.Failed(
                    CliExecutionResult.Failure(CliExitCode.TransportFailure, "BrightSync resident command authentication failed."));
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                return ResidentSendResult.NotRunning();

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            var commandResponse = JsonSerializer.Deserialize(responseText, CliJsonContext.Default.CommandResponse);
            if (commandResponse == null)
            {
                return ResidentSendResult.Failed(
                    CliExecutionResult.Failure(CliExitCode.TransportFailure, "BrightSync returned an invalid command response."));
            }

            return response.IsSuccessStatusCode
                ? ResidentSendResult.Success(commandResponse.ToExecutionResult())
                : ResidentSendResult.Failed(commandResponse.ToExecutionResult());
        }
        catch (HttpRequestException ex) when (ex.InnerException is WebException ||
                                              ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug(ex, "Resident command server was unreachable");
            return ResidentSendResult.NotRunning();
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "Resident command request to {BaseUrl}{Path} timed out", serverInfo.BaseUrl, relativePath);
            return ResidentSendResult.NotRunning();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Resident command request to {BaseUrl}{Path} failed", serverInfo.BaseUrl, relativePath);
            return ResidentSendResult.Failed(
                CliExecutionResult.Failure(CliExitCode.TransportFailure, "Failed to contact the running BrightSync instance."));
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void DeleteMetadata(string metadataPath)
    {
        try
        {
            if (File.Exists(metadataPath))
                File.Delete(metadataPath);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to delete stale resident command metadata {MetadataPath}", metadataPath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _httpClient.Dispose();
    }

    private enum ResidentSendStatus
    {
        NotRunning,
        Success,
        Failed
    }

    private sealed class ResidentSendResult(ResidentSendStatus status, CliExecutionResult result)
    {
        public ResidentSendStatus Status { get; } = status;
        public CliExecutionResult Result { get; } = result;

        public static ResidentSendResult NotRunning()
            => new(ResidentSendStatus.NotRunning,
                CliExecutionResult.Failure(CliExitCode.TransportFailure, "BrightSync is not running."));

        public static ResidentSendResult Success(CliExecutionResult result)
            => new(ResidentSendStatus.Success, result);

        public static ResidentSendResult Failed(CliExecutionResult result)
            => new(ResidentSendStatus.Failed, result);
    }
}
