using System.Net;
using System.Net.Http;
using System.Text.Json;
using BrightSync.Cli;

namespace BrightSync.Tests;

public sealed class ResidentCommandClientTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "BrightSyncTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TryDispatchAsync_forwards_command_when_metadata_and_token_are_valid()
    {
        Directory.CreateDirectory(_tempDirectory);
        var metadataPath = Path.Combine(_tempDirectory, "command-server.json");
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(
                new CommandServerInfo
                {
                    BaseUrl = "http://127.0.0.1:45137/",
                    BearerToken = "secret",
                    Pid = Environment.ProcessId,
                    StartedUtc = DateTime.UtcNow
                },
                CliJsonContext.Default.CommandServerInfo));

        using var client = new ResidentCommandClient(
            new HttpClient(new FakeHttpMessageHandler(request =>
            {
                var token = request.Headers.Authorization?.Parameter;
                if (request.RequestUri!.AbsolutePath == "/v1/ping")
                    return Task.FromResult(CreateJsonResponse(HttpStatusCode.OK, CommandResponse.Ok($"pong:{token}")));

                return Task.FromResult(CreateJsonResponse(
                    HttpStatusCode.OK,
                    CommandResponse.Ok($"Brightness set with token {token}.", appliedBrightness: 55)));
            })),
            metadataPath);

        var result = await client.TryDispatchAsync(
            new AppCommand(AppCommandType.BrightnessSet, brightnessValue: 55),
            CancellationToken.None);

        Assert.Equal(ResidentCommandDispatchStatus.Success, result.Status);
        Assert.NotNull(result.Result);
        Assert.Equal(CliExitCode.Success, result.Result!.ExitCode);
        Assert.Equal("Brightness set with token secret.", result.Result.Message);
    }

    [Fact]
    public async Task TryDispatchAsync_forwards_status_json_flag_and_returns_redacted_source_generated_json()
    {
        Directory.CreateDirectory(_tempDirectory);
        var metadataPath = Path.Combine(_tempDirectory, "command-server.json");
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(
                new CommandServerInfo
                {
                    BaseUrl = "http://127.0.0.1:45137/",
                    BearerToken = "secret",
                    Pid = Environment.ProcessId,
                    StartedUtc = DateTime.UtcNow
                },
                CliJsonContext.Default.CommandServerInfo));

        string? commandPayload = null;
        using var client = new ResidentCommandClient(
            new HttpClient(new FakeHttpMessageHandler(async request =>
            {
                if (request.RequestUri!.AbsolutePath == "/v1/ping")
                    return CreateJsonResponse(HttpStatusCode.OK, CommandResponse.Ok("pong"));

                commandPayload = await request.Content!.ReadAsStringAsync();
                return CreateJsonResponse(
                    HttpStatusCode.OK,
                    CommandResponse.Ok(
                        "status",
                        status: new CliStatusSnapshot
                        {
                            SchemaVersion = 1,
                            Version = "0.17",
                            TimestampUtc = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero),
                            MasterBrightness = 62,
                            AutomaticBrightnessEnabled = true,
                            EyeProtectionActive = false,
                            BrightnessBoostActive = true,
                            BrightnessBoostRemainingSeconds = 120,
                            MonitorCount = 2,
                            ControllableMonitorCount = 2
                        }));
            })),
            metadataPath);

        var result = await client.TryDispatchAsync(
            new AppCommand(AppCommandType.Status, jsonOutput: true),
            CancellationToken.None);

        Assert.Equal(ResidentCommandDispatchStatus.Success, result.Status);
        Assert.NotNull(commandPayload);
        var requestPayload = JsonSerializer.Deserialize(commandPayload!, CliJsonContext.Default.CommandRequest);
        Assert.NotNull(requestPayload);
        Assert.Equal(AppCommandType.Status, requestPayload!.CommandType);
        Assert.True(requestPayload.JsonOutput);

        using var json = JsonDocument.Parse(result.Result!.Message);
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(62, json.RootElement.GetProperty("masterBrightness").GetInt32());
        Assert.Equal(120, json.RootElement.GetProperty("brightnessBoostRemainingSeconds").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("controllableMonitorCount").GetInt32());
        Assert.DoesNotContain("secret", result.Result.Message);
        Assert.DoesNotContain("BearerToken", result.Result.Message);
    }

    [Fact]
    public async Task TryDispatchAsync_returns_failed_when_server_rejects_token()
    {
        Directory.CreateDirectory(_tempDirectory);
        var metadataPath = Path.Combine(_tempDirectory, "command-server.json");
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(
                new CommandServerInfo
                {
                    BaseUrl = "http://127.0.0.1:45137/",
                    BearerToken = "wrong",
                    Pid = Environment.ProcessId,
                    StartedUtc = DateTime.UtcNow
                },
                CliJsonContext.Default.CommandServerInfo));

        using var client = new ResidentCommandClient(
            new HttpClient(new FakeHttpMessageHandler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))),
            metadataPath);

        var result = await client.TryDispatchAsync(
            new AppCommand(AppCommandType.BrightnessSet, brightnessValue: 55),
            CancellationToken.None);

        Assert.Equal(ResidentCommandDispatchStatus.Failed, result.Status);
        Assert.NotNull(result.Result);
        Assert.Equal(CliExitCode.TransportFailure, result.Result!.ExitCode);
    }

    [Fact]
    public void TryReadServerInfo_returns_null_and_deletes_stale_metadata_for_dead_process()
    {
        Directory.CreateDirectory(_tempDirectory);
        var metadataPath = Path.Combine(_tempDirectory, "command-server.json");
        File.WriteAllText(
            metadataPath,
            JsonSerializer.Serialize(
                new CommandServerInfo
                {
                    BaseUrl = "http://127.0.0.1:45137/",
                    BearerToken = "secret",
                    Pid = int.MaxValue,
                    StartedUtc = DateTime.UtcNow
                },
                CliJsonContext.Default.CommandServerInfo));

        var result = ResidentCommandClient.TryReadServerInfo(metadataPath);

        Assert.Null(result);
        Assert.False(File.Exists(metadataPath));
    }

    [Fact]
    public async Task TryDispatchAsync_treats_transport_failure_as_not_running()
    {
        Directory.CreateDirectory(_tempDirectory);
        var metadataPath = Path.Combine(_tempDirectory, "command-server.json");
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(
                new CommandServerInfo
                {
                    BaseUrl = "http://127.0.0.1:45137/",
                    BearerToken = "secret",
                    Pid = Environment.ProcessId,
                    StartedUtc = DateTime.UtcNow
                },
                CliJsonContext.Default.CommandServerInfo));

        using var client = new ResidentCommandClient(
            new HttpClient(new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"))),
            metadataPath);

        var result = await client.TryDispatchAsync(
            new AppCommand(AppCommandType.BrightnessSet, brightnessValue: 55),
            CancellationToken.None);

        Assert.Equal(ResidentCommandDispatchStatus.NotRunning, result.Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, CommandResponse response)
        => new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(response, CliJsonContext.Default.CommandResponse))
        };

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request);
    }
}
