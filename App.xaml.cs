using System.Windows;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using BrightSync.Core.Updates;
using BrightSync.UI;

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

        _updateChecker = new UpdateChecker(configManager);
        _updateChecker.Start();

        _trayManager = new TrayManager(_syncEngine, configManager, _ddcService);
        _trayManager.ExitRequested += (_, _) => ExitApp();
        _trayManager.Initialize();

        // Open settings on manual launch; stay hidden on auto-start
        var isAutoStart = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        if (!isAutoStart)
            _trayManager.ShowSettings();
    }

    private void ExitApp()
    {
        _syncEngine.Dispose();
        _trayManager.Dispose();
        _ddcService.Dispose();
        _updateChecker.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _syncEngine?.Dispose();
        _trayManager?.Dispose();
        _ddcService?.Dispose();
        _updateChecker?.Dispose();
        base.OnExit(e);
    }
}
