using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BrightSync.Cli;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using BrightSync.Core.Updates;
using BrightSync.UI;
using Serilog;

namespace BrightSync;

public partial class App : Application
{
    private AutoBrightnessService? _autoBrightnessService;
    private BrightnessBoostService? _brightnessBoostService;
    private DdcCiService? _ddcService;
    private EyeProtectionService? _eyeProtectionService;
    private IdleReductionService? _idleReductionService;
    private PowerSavingService? _powerSavingService;
    private ResidentCommandServer? _residentCommandServer;
    private SelfUpdateService? _selfUpdateService;
    private BrightSyncEngine? _syncEngine;
    private TrayManager? _trayManager;
    private UpdateChecker? _updateChecker;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = this;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Set shutdown mode to explicit since this is a tray application
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            Log.Information("Application starting. BaseDirectory={BaseDirectory}", AppContext.BaseDirectory);

            SetupExceptionHandling();

            // Setup services
            var configManager = new ConfigManager();
            _ddcService = new DdcCiService(configManager);
            var watcher = new InternalBrightnessWatcher();

            _syncEngine = new BrightSyncEngine(_ddcService, watcher, configManager);
            _syncEngine.Start();
            Log.Information("Brightness sync engine started");

            _autoBrightnessService = new AutoBrightnessService(_syncEngine, configManager);
            _autoBrightnessService.Start();
            Log.Information("Auto brightness service started");

            _idleReductionService = new IdleReductionService(_syncEngine, configManager);
            _idleReductionService.Start();
            Log.Information("Idle reduction service started");

            _powerSavingService = new PowerSavingService(_syncEngine, configManager);
            _syncEngine.SetPowerSavingService(_powerSavingService);
            _powerSavingService.Start();
            Log.Information("Power saving service started");

            _eyeProtectionService = new EyeProtectionService(_syncEngine, configManager);
            _brightnessBoostService = new BrightnessBoostService(_syncEngine, configManager);
            _eyeProtectionService.SetBrightnessBoostService(_brightnessBoostService);
            _brightnessBoostService.SetEyeProtectionService(_eyeProtectionService);
            _syncEngine.SetEyeProtectionService(_eyeProtectionService);
            _syncEngine.SetBrightnessBoostService(_brightnessBoostService);

            if (configManager.Config.EyeProtectionEnabled && configManager.Config.BrightnessBoostEnabled)
            {
                Log.Warning(
                    "Both eye protection and brightness boost were enabled in config; disabling eye protection to restore a valid state");
                configManager.Config.EyeProtectionEnabled = false;
                configManager.Config.EyeProtectionEndUtc = null;
                configManager.Save();
            }

            _eyeProtectionService.Start();
            Log.Information("Eye protection service started");
            _brightnessBoostService.Start();
            Log.Information("Brightness boost service started");

            _updateChecker = new UpdateChecker(configManager);
            _updateChecker.Start();
            Log.Information("Update checker started");

            _selfUpdateService = new SelfUpdateService(_updateChecker, _idleReductionService, configManager);
            _selfUpdateService.UpdateDownloaded += (_, message) =>
                Dispatcher.UIThread.Post(() => _trayManager?.ShowUpdateNotification(message));
            _selfUpdateService.InstallStarted += (_, _) =>
                Dispatcher.UIThread.Post(() => _trayManager?.ShowUpdateNotification("Installing update...",
                    "BrightSync Update"));
            _selfUpdateService.InstallCompleted += (_, _) =>
                Dispatcher.UIThread.Post(() => _trayManager?.ShowUpdateNotification("Update installed",
                    "BrightSync Update"));
            _selfUpdateService.InstallFailed += (_, error) =>
                Dispatcher.UIThread.Post(() => _trayManager?.ShowUpdateNotification(error, "Update Failed"));
            _selfUpdateService.Start();
            Log.Information("Self-update service started");

            // Initialize Tray Manager
            _trayManager = new TrayManager(
                _syncEngine,
                _autoBrightnessService,
                _idleReductionService,
                _eyeProtectionService,
                _brightnessBoostService,
                configManager,
                _ddcService,
                _updateChecker,
                _selfUpdateService);

            _trayManager.ExitRequested += (_, _) => ExitApp();
            _trayManager.Initialize();
            Log.Information("Tray manager initialized");

            _residentCommandServer = new ResidentCommandServer(
                new ResidentCommandHandler(
                    _syncEngine,
                    _autoBrightnessService,
                    _eyeProtectionService,
                    _brightnessBoostService,
                    _trayManager,
                    ExitApp));
            _residentCommandServer.Start();

            // Open settings on manual launch; stay hidden on auto-start
            var isAutoStart = Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
            Log.Information("Launch mode: {LaunchMode}", isAutoStart ? "AutoStart" : "Manual");
            if (!isAutoStart)
                _trayManager.ShowSettings();

            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupExceptionHandling()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                Log.Fatal(exception, "Unhandled AppDomain exception. IsTerminating={IsTerminating}",
                    args.IsTerminating);
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
    }

    private void ExitApp()
    {
        Log.Information("Exit requested");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Log.Information("Application exiting with code {ExitCode}", e.ApplicationExitCode);

        _autoBrightnessService?.Dispose();
        _idleReductionService?.Dispose();
        _powerSavingService?.Dispose();
        _eyeProtectionService?.Dispose();
        _brightnessBoostService?.Dispose();
        _residentCommandServer?.Dispose();
        _selfUpdateService?.Dispose();
        _syncEngine?.Dispose();
        _trayManager?.Dispose();
        _ddcService?.Dispose();
        _updateChecker?.Dispose();
        Log.CloseAndFlush();
    }
}