using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
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
    private AutoBrightnessService? _autoBrightnessService;
    private BrightnessBoostService? _brightnessBoostService;
    private DdcCiService? _ddcService;
    private EyeProtectionService? _eyeProtectionService;
    private IdleReductionService? _idleReductionService;
    private PowerSavingService? _powerSavingService;
    private BrightSyncEngine? _syncEngine;
    private TrayManager? _trayManager;
    private UpdateChecker? _updateChecker;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            LoggingSetup.Initialize();
            Log.Information("Application starting. BaseDirectory={BaseDirectory}", AppContext.BaseDirectory);

            SetupExceptionHandling();

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

            // Construct tray icon in code so Native AOT cannot strip it
            var trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://BrightSync/Resources/app.png"))),
                ToolTipText = "BrightSync - Running"
            };
            TrayIcon.SetIcons(this, new TrayIcons { trayIcon });
            Log.Information("Tray icon registered");

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

            trayIcon.Command = _trayManager.ViewModel.ToggleQuickPopupCommand;
            trayIcon.Menu = BuildTrayMenu(_trayManager.ViewModel);

            var isAutoStart = Environment.GetCommandLineArgs()
                .Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
            Log.Information("Launch mode: {LaunchMode}", isAutoStart ? "AutoStart" : "Manual");
            if (!isAutoStart)
                _trayManager.ShowSettings();

            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static NativeMenu BuildTrayMenu(TrayMenuViewModel vm)
    {
        var eyeProtectionMenu = new NativeMenu();
        eyeProtectionMenu.Add(new NativeMenuItem
            { Header = vm.EyeProtectionToggleHeader, Command = vm.ToggleEyeProtectionCommand });
        eyeProtectionMenu.Add(new NativeMenuItemSeparator());
        foreach (var hours in new[] { 1, 2, 3, 4, 8, 12, 24 })
            eyeProtectionMenu.Add(new NativeMenuItem
            {
                Header = $"{hours} hour{(hours > 1 ? "s" : "")}", Command = vm.SetEyeProtectionPresetCommands[hours]
            });

        var brightnessBoostMenu = new NativeMenu();
        brightnessBoostMenu.Add(new NativeMenuItem
            { Header = vm.BrightnessBoostToggleHeader, Command = vm.ToggleBrightnessBoostCommand });
        brightnessBoostMenu.Add(new NativeMenuItemSeparator());
        foreach (var hours in new[] { 1, 2, 3, 4, 8, 12, 24 })
            brightnessBoostMenu.Add(new NativeMenuItem
            {
                Header = $"{hours} hour{(hours > 1 ? "s" : "")}", Command = vm.SetBrightnessBoostPresetCommands[hours]
            });

        var menu = new NativeMenu();
        menu.Add(new NativeMenuItem { Header = "Settings", Command = vm.OpenSettingsCommand });
        menu.Add(new NativeMenuItem { Header = "Eye Protection", Menu = eyeProtectionMenu });
        menu.Add(new NativeMenuItem { Header = "Brightness Boost", Menu = brightnessBoostMenu });
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(new NativeMenuItem { Header = "Refresh Monitors", Command = vm.RefreshMonitorsCommand });
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(new NativeMenuItem { Header = "Exit", Command = vm.ExitCommand });
        return menu;
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