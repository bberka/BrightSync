using System.Windows;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Logging;
using BrightSync.Core.Monitors;
using BrightSync.Core.Updates;
using BrightSync.UI;
using Serilog;

namespace BrightSync;

public partial class App
{
    private TrayManager _trayManager = null!;
    private BrightSyncEngine _syncEngine = null!;
    private DdcCiService _ddcService = null!;
    private UpdateChecker _updateChecker = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LoggingSetup.Initialize();
        Log.Information("Application starting. BaseDirectory={BaseDirectory}", AppContext.BaseDirectory);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled UI exception");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                Log.Fatal(exception, "Unhandled AppDomain exception. IsTerminating={IsTerminating}", args.IsTerminating);
            }
            else
            {
                Log.Fatal("Unhandled AppDomain exception. IsTerminating={IsTerminating}; PayloadType={PayloadType}",
                    args.IsTerminating,
                    args.ExceptionObject?.GetType().FullName ?? "<null>");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        // Follow system theme instead of forcing Dark
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme()
                is Wpf.Ui.Appearance.SystemTheme.Dark
                ? Wpf.Ui.Appearance.ApplicationTheme.Dark
                : Wpf.Ui.Appearance.ApplicationTheme.Light);

        var configManager = new ConfigManager();
        _ddcService = new DdcCiService();
        var watcher = new InternalBrightnessWatcher();

        _syncEngine = new BrightSyncEngine(_ddcService, watcher, configManager);
        _syncEngine.Start();
        Log.Information("Brightness sync engine started");

        _updateChecker = new UpdateChecker(configManager);
        _updateChecker.Start();
        Log.Information("Update checker started");

        _trayManager = new TrayManager(_syncEngine, configManager, _ddcService);
        _trayManager.ExitRequested += (_, _) => ExitApp();
        _trayManager.Initialize();
        Log.Information("Tray manager initialized");

        // Open settings on manual launch; stay hidden on auto-start
        var isAutoStart = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        Log.Information("Launch mode: {LaunchMode}", isAutoStart ? "AutoStart" : "Manual");
        if (!isAutoStart)
            _trayManager.ShowSettings();
    }

    private void ExitApp()
    {
        Log.Information("Exit requested");
        _syncEngine.Dispose();
        _trayManager.Dispose();
        _ddcService.Dispose();
        _updateChecker.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Application exiting with code {ExitCode}", e.ApplicationExitCode);
        _syncEngine?.Dispose();
        _trayManager?.Dispose();
        _ddcService?.Dispose();
        _updateChecker?.Dispose();
        base.OnExit(e);
        Log.CloseAndFlush();
    }
}
