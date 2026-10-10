using Avalonia;
using BrightSync.Cli;
using BrightSync.Core.Logging;
using BrightSync.Platform;
using Serilog;

namespace BrightSync;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        PlatformServices.Current = PlatformBootstrap.Create();

        var parseResult = CliParser.Parse(args);
        if (!parseResult.IsSuccess)
        {
            var feedback = new CliFeedback();
            feedback.AttachToParentConsole();
            LoggingSetup.Initialize(enableConsoleOutput: false);
            feedback.WriteError(parseResult.ErrorMessage ?? "BrightSync received invalid arguments.");
            Log.Warning("Invalid CLI arguments: {Message}", parseResult.ErrorMessage);
            Log.CloseAndFlush();
            return (int)CliExitCode.InvalidArguments;
        }

        if (parseResult.IsCliInvocation && parseResult.Command != null)
            return RunCliCommand(parseResult.Command);

        LoggingSetup.Initialize();

        // The guard stops a second BrightSync instance. It is acquired up front so a duplicate
        // launch can show a single native message and exit before any Avalonia state is created.
        using var singleInstance = PlatformServices.Current.CreateSingleInstanceGuard();
        if (!singleInstance.TryAcquire())
        {
            PlatformServices.Current.Shell.ShowMessage("BrightSync", "BrightSync is already running.");
            return 1;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            // Avalonia surfaces fatal startup errors through the dispatcher; this
            // catch guarantees a non-zero exit code and a Serilog line so the
            // crash shows up in the rolling log file even when no UI is up.
            Log.Fatal(ex, "BrightSync terminated with an unhandled exception");
            return 2;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static int RunCliCommand(AppCommand command)
    {
        var feedback = new CliFeedback();
        feedback.AttachToParentConsole();
        LoggingSetup.Initialize(enableConsoleOutput: false);

        try
        {
            using var residentClient = new ResidentCommandClient();
            var router = new CliCommandRouter(residentClient, new OneShotCommandExecutor());
            var result = router.RouteAsync(command, CancellationToken.None).GetAwaiter().GetResult();
            if (result.IsError)
                feedback.WriteError(result.Message);
            else
                feedback.WriteInfo(result.Message);

            Log.Information("CLI command {CommandType} finished with exit code {ExitCode}",
                command.CommandType,
                (int)result.ExitCode);
            return (int)result.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "BrightSync CLI command {CommandType} failed", command.CommandType);
            feedback.WriteError("BrightSync failed to process the command.");
            return (int)CliExitCode.TransportFailure;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
