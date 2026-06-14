using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace BrightSync.Cli;

public sealed class ResidentCommandServer : IDisposable
{
    private const string BaseUrl = "http://127.0.0.1:45137/";

    private readonly ResidentCommandHandler _handler;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _metadataPath;
    private string? _bearerToken;
    private Task? _listenTask;
    private bool _disposed;

    public ResidentCommandServer(ResidentCommandHandler handler, string? metadataPath = null)
    {
        _handler = handler;
        _metadataPath = metadataPath ?? CommandServerPaths.MetadataFilePath;
        _listener.Prefixes.Add(BaseUrl);
    }

    public void Start()
    {
        if (_listener.IsListening)
            return;

        _bearerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            _listener.Start();
            WriteMetadata();
            _listenTask = Task.Run(ListenLoopAsync);
            Log.Information("Resident command server started at {BaseUrl}", BaseUrl);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to start resident command server");
            CleanupMetadata();
        }
    }

    private async Task ListenLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleContextAsync(context, _shutdown.Token), _shutdown.Token);
            }
            catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Resident command server accept loop failed");
                if (context != null)
                    context.Response.Abort();
            }
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsAuthorized(context.Request))
            {
                await WriteResponseAsync(context.Response,
                    CommandResponse.Error(CliExitCode.TransportFailure, "Unauthorized BrightSync command request."),
                    HttpStatusCode.Unauthorized,
                    cancellationToken);
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(context.Response,
                    CommandResponse.Error(CliExitCode.InvalidArguments, "BrightSync commands require HTTP POST."),
                    HttpStatusCode.MethodNotAllowed,
                    cancellationToken);
                return;
            }

            if (path.Equals("/v1/ping", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(context.Response, CommandResponse.Ok("BrightSync is running."),
                    HttpStatusCode.OK, cancellationToken);
                return;
            }

            if (!path.Equals("/v1/commands", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(context.Response,
                    CommandResponse.Error(CliExitCode.InvalidArguments, "Unknown BrightSync command endpoint."),
                    HttpStatusCode.NotFound,
                    cancellationToken);
                return;
            }

            using var streamReader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            var body = await streamReader.ReadToEndAsync(cancellationToken);
            var request = JsonSerializer.Deserialize(body, CliJsonContext.Default.CommandRequest);
            if (request == null)
            {
                await WriteResponseAsync(context.Response,
                    CommandResponse.Error(CliExitCode.InvalidArguments, "BrightSync received an invalid command payload."),
                    HttpStatusCode.BadRequest,
                    cancellationToken);
                return;
            }

            var response = await _handler.HandleAsync(request, cancellationToken);
            var statusCode = response.Success ? HttpStatusCode.OK : HttpStatusCode.BadRequest;
            await WriteResponseAsync(context.Response, response, statusCode, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Resident command server request handling failed");
            if (context.Response.OutputStream.CanWrite)
            {
                await WriteResponseAsync(context.Response,
                    CommandResponse.Error(CliExitCode.ResidentCommandFailed, "BrightSync failed to process the command."),
                    HttpStatusCode.InternalServerError,
                    cancellationToken);
            }
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var authHeader = request.Headers["Authorization"];
        return !string.IsNullOrWhiteSpace(_bearerToken) &&
               authHeader == $"Bearer {_bearerToken}";
    }

    private void WriteMetadata()
    {
        CommandServerPaths.EnsureDirectory();
        var info = new CommandServerInfo
        {
            BaseUrl = BaseUrl,
            BearerToken = _bearerToken ?? string.Empty,
            Pid = Environment.ProcessId,
            StartedUtc = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(info, CliJsonContext.Default.CommandServerInfo);
        File.WriteAllText(_metadataPath, json, Encoding.UTF8);
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, CommandResponse payload,
        HttpStatusCode statusCode, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, CliJsonContext.Default.CommandResponse);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }

    private void CleanupMetadata()
    {
        try
        {
            if (File.Exists(_metadataPath))
                File.Delete(_metadataPath);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to delete resident command metadata file {MetadataPath}", _metadataPath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _shutdown.Cancel();
        CleanupMetadata();
        if (_listener.IsListening)
            _listener.Stop();
        _listener.Close();
        try
        {
            _listenTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            Log.Debug(ex, "Resident command server shutdown wait surfaced an exception");
        }
        _shutdown.Dispose();
    }
}
