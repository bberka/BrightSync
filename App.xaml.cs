using System.Windows;
using BrightnessSync.Core.Brightness;
using BrightnessSync.Core.Config;
using BrightnessSync.Core.Monitors;
using BrightnessSync.UI;

namespace BrightnessSync;

// Base class is declared in App.g.cs (System.Windows.Application) — not repeated here to avoid
// the WpfApplication vs WinForms.Application ambiguity introduced by UseWindowsForms=true.
public partial class App
{
    // Assigned together in OnStartup, all non-null after that.
    private TrayManager _trayManager = null!;
    private BrightnessSyncEngine _syncEngine = null!;
    private DdcCiService _ddcService = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);

        var configManager = new ConfigManager();
        _ddcService = new DdcCiService();
        var watcher = new InternalBrightnessWatcher();

        _syncEngine = new BrightnessSyncEngine(_ddcService, watcher, configManager);
        _syncEngine.Start();

        _trayManager = new TrayManager(_syncEngine, configManager, _ddcService);
        _trayManager.ExitRequested += (_, _) => ExitApp();
        _trayManager.Initialize();
    }

    private void ExitApp()
    {
        _syncEngine.Dispose();
        _trayManager.Dispose();
        _ddcService.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Guard: OnExit may fire before OnStartup finishes in edge cases.
        _syncEngine?.Dispose();
        _trayManager?.Dispose();
        _ddcService?.Dispose();
        base.OnExit(e);
    }
}
