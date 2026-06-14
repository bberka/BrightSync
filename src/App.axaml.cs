using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Logging;
using BrightSync.Core.Monitors;
using BrightSync.Core.Updates;
using BrightSync.UI;
using Serilog;

namespace BrightSync;

public partial class App : Application
{
    private TrayManager? _trayManager;
    private BrightSyncEngine? _syncEngine;
    private AutoBrightnessService? _autoBrightnessService;
    private IdleReductionService? _idleReductionService;
    private PowerSavingService? _powerSavingService;
    private EyeProtectionService? _eyeProtectionService;
    private BrightnessBoostService? _brightnessBoostService;
    private DdcCiService? _ddcService;
    private UpdateChecker? _updateChecker;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Set shutdown mode to explicit since this is a tray application
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Initialize Logging
            LoggingSetup.Initialize();
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

            // Initialize Tray Manager
            var trayIcon = TrayIcon.GetIcons(this)?.OfType<TrayIcon>().FirstOrDefault()
                ?? throw new InvalidOperationException("Tray icon was not declared in App.axaml");
            _trayManager = new TrayManager(
                trayIcon,
                _syncEngine,
                _autoBrightnessService,
                _idleReductionService,
                _eyeProtectionService,
                _brightnessBoostService,
                configManager,
                _ddcService,
                _updateChecker);

            _trayManager.ExitRequested += (_, _) => ExitApp();
            DataContext = _trayManager.ViewModel;
            Log.Information("Tray manager initialized");

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
        _syncEngine?.Dispose();
        _trayManager?.Dispose();
        _ddcService?.Dispose();
        _updateChecker?.Dispose();
        Log.CloseAndFlush();
    }
}