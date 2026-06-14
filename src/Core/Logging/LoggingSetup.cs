using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace BrightSync.Core.Logging;

public static class LoggingSetup
{
    private static readonly string AppDataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrightSync");

    public static string GetLogDirectory()
        => Path.Combine(AppDataDirectory, "Logs");

    public static void Initialize()
    {
        var logDirectory = GetLogDirectory();
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(logDirectory, "brightsync.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(new ConsoleAndDebugSink())
            .CreateLogger();
    }

    private sealed class ConsoleAndDebugSink : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            var message = logEvent.RenderMessage();
            var levelString = logEvent.Level.ToString();
            var level = levelString.Substring(0, Math.Min(3, levelString.Length)).ToUpper();
            var timestamp = logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var exception = logEvent.Exception != null ? $"{Environment.NewLine}{logEvent.Exception}" : "";
            var formatted = $"{timestamp} [{level}] {message}{exception}";

            Console.WriteLine(formatted);
            System.Diagnostics.Debug.WriteLine(formatted);
        }
    }
}
