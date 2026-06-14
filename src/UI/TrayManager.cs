using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using BrightSync.Core.Updates;
using BrightSync.UI.ViewModels;
using BrightSync.UI.Views;
using Serilog;

namespace BrightSync.UI;

/// <summary>
/// Manages the system tray icon, native context menu, quick brightness popup, and the settings window (AOT compatible).
/// Left-click toggles the quick brightness popup; right-click shows the native context menu.
/// </summary>
public sealed class TrayManager(
    BrightSyncEngine engine,
    AutoBrightnessService autoBrightness,
    IdleReductionService idleReduction,
    EyeProtectionService eyeProtection,
    BrightnessBoostService brightnessBoost,
    ConfigManager config,
    DdcCiService ddc,
    UpdateChecker updateChecker)
    : IDisposable
{
    private bool _disposed;
    private DateTime _lastPopupClosed = DateTime.MinValue;
    private QuickBrightnessWindow? _quickPopup;
    private DateTime _quickPopupShownAt = DateTime.MinValue;
    private QuickBrightnessViewModel? _quickVm;
    private int _refreshInProgress;
    private SettingsWindow? _settingsWindow;

    private WindowsTrayIcon? _trayIcon;


    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Debug("Disposing tray manager");
        _quickVm?.Dispose();
        _quickPopup?.Close();

        if (_trayIcon != null)
        {
            _trayIcon.Dispose();
        }
    }

    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        _trayIcon = new WindowsTrayIcon();
        _trayIcon.Clicked += (_, _) => ToggleQuickPopup();
        _trayIcon.SettingsRequested += (_, _) => ShowSettings();
        _trayIcon.RefreshRequested += (_, _) => RefreshMonitors();
        _trayIcon.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        _trayIcon.EyeProtectionToggleRequested += (_, _) => eyeProtection.SetEnabled(!eyeProtection.IsEnabled);
        _trayIcon.BrightnessBoostToggleRequested += (_, _) => brightnessBoost.SetEnabled(!brightnessBoost.IsEnabled);
        _trayIcon.EyeProtectionPresetRequested += (_, hours) => eyeProtection.SetEnabled(true, hours);
        _trayIcon.BrightnessBoostPresetRequested += (_, hours) => brightnessBoost.SetEnabled(true, hours);
        _trayIcon.Initialize(BuildTrayToolTip(), eyeProtection.IsEnabled, brightnessBoost.IsEnabled);

        RefreshTrayMenu();

        engine.MasterBrightnessChanged += (_, _) => RefreshTrayToolTip();
        autoBrightness.StateChanged += (_, _) => RefreshTrayToolTip();
        eyeProtection.StateChanged += (_, _) =>
        {
            RefreshTrayMenu();
            RefreshTrayToolTip();
        };
        brightnessBoost.StateChanged += (_, _) =>
        {
            RefreshTrayMenu();
            RefreshTrayToolTip();
        };

        RefreshTrayToolTip();
    }

    private void RefreshTrayMenu()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon == null) return;
            _trayIcon.UpdateMenuState(eyeProtection.IsEnabled, brightnessBoost.IsEnabled);
        });
    }

    private void RefreshTrayToolTip()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon == null) return;
            _trayIcon.SetToolTip(BuildTrayToolTip());
        });
    }

    private string BuildTrayToolTip()
    {
        var brightness = autoBrightness.IsEnabled ? autoBrightness.GetCurrentBrightness() : engine.MasterBrightness;
        var safeBrightness = brightness >= 0 ? brightness : 50;
        var mode = autoBrightness.IsEnabled ? "Auto" : "Manual";

        if (eyeProtection.IsEnabled)
            return $"BrightSync - Global brightness: {safeBrightness}% | {mode} | Eye Protection";

        if (brightnessBoost.IsEnabled)
            return $"BrightSync - Global brightness: {safeBrightness}% | {mode} | Boost";

        return $"BrightSync - Global brightness: {safeBrightness}% | {mode}";
    }


    /// <summary>Opens the full settings window (used by context menu and quick popup).</summary>
    public void ShowSettings()
    {
        Log.Information("Opening settings window");
        Dispatcher.UIThread.Post(() =>
        {
            _quickPopup?.Hide();

            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(engine, autoBrightness, idleReduction, eyeProtection,
                    brightnessBoost, config, ddc, updateChecker);
                _settingsWindow.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
                _settingsWindow.Show();
            }
            else if (!_settingsWindow.IsVisible)
            {
                _settingsWindow.Show();
                _settingsWindow.PositionBottomRight();
                _settingsWindow.Activate();
            }
            else
            {
                _settingsWindow.Activate();
            }
        });
    }

    private void ToggleQuickPopup()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_quickPopup?.IsVisible == true)
            {
                if ((DateTime.UtcNow - _quickPopupShownAt).TotalMilliseconds < 750)
                {
                    Log.Debug("Ignoring tray toggle because quick brightness popup just opened");
                    return;
                }

                Log.Debug("Hiding quick brightness popup");
                _quickPopup.Hide();
                return;
            }

            // Prevent re-show if just closed by deactivation (tray icon click steals focus)
            if ((DateTime.UtcNow - _lastPopupClosed).TotalMilliseconds < 300)
            {
                Log.Debug("Quick brightness popup reopen suppressed due to recent close");
                return;
            }

            ShowQuickPopup();
        });
    }

    private void ShowQuickPopup()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            Log.Debug("Hiding settings window to show quick brightness popup");
            _settingsWindow.Hide();
        }

        if (_quickPopup == null)
        {
            _quickVm = new QuickBrightnessViewModel(engine, autoBrightness, eyeProtection, brightnessBoost, ddc, config,
                ShowSettings);
            _quickPopup = new QuickBrightnessWindow { DataContext = _quickVm };
            _quickPopup.Deactivated += (_, _) => HideQuickPopupAfterDeactivation();

            _quickPopup.SizeChanged += (_, _) =>
            {
                if (_quickPopup.IsVisible)
                    PositionWindowAboveTray(_quickPopup);
            };

            _quickPopup.PropertyChanged += (s, e) =>
            {
                if (e.Property == Window.IsVisibleProperty && e.NewValue is false)
                    _lastPopupClosed = DateTime.UtcNow;
            };
        }
        else
        {
            _quickVm?.Refresh();
        }

        _quickPopup.Opacity = 0;
        _quickPopupShownAt = DateTime.UtcNow;
        _quickPopup.Show();
        _quickPopup.UpdateLayout();
        PositionWindowAboveTray(_quickPopup);
        _quickPopup.Opacity = 1;
        _quickPopup.Activate();
        Log.Debug("Quick brightness popup shown");
    }

    private void HideQuickPopupAfterDeactivation()
    {
        if (_quickPopup?.IsVisible != true)
            return;

        if ((DateTime.UtcNow - _quickPopupShownAt).TotalMilliseconds < 500)
        {
            Log.Debug("Quick brightness popup initial deactivation ignored");
            return;
        }

        Log.Debug("Hiding quick brightness popup after deactivation");
        _quickPopup.Hide();
    }

    private void PositionWindowAboveTray(Window window)
    {
        var screen = window.Screens.ScreenFromVisual(window) ?? window.Screens.Primary;
        if (screen == null) return;

        var workingArea = screen.WorkingArea;
        var scaling = screen.Scaling;

        var windowPhysicalWidth = (int)(window.FrameSize?.Width ?? (window.Bounds.Width * scaling));
        var windowPhysicalHeight = (int)(window.FrameSize?.Height ?? (window.Bounds.Height * scaling));

        if (windowPhysicalWidth <= 0) windowPhysicalWidth = (int)(window.Width * scaling);
        if (windowPhysicalHeight <= 0) windowPhysicalHeight = (int)(window.Height * scaling);

        var x = workingArea.Right - windowPhysicalWidth - (int)(12 * scaling);
        var y = workingArea.Bottom - windowPhysicalHeight - (int)(12 * scaling);

        window.Position = new PixelPoint(x, y);
    }

    private void RefreshMonitors()
    {
        Log.Information("Tray action requested monitor refresh");
        RefreshMonitorsCore(showBalloonTip: true, statusText: "Refreshing monitors...");
    }

    public void HandleDisplayConfigurationChanged()
    {
        Log.Information("Refreshing monitor UI after display configuration change");
        RefreshMonitorsCore(showBalloonTip: false, statusText: "Displays changed. Refreshing monitors...");
    }

    private void RefreshMonitorsCore(bool showBalloonTip, string statusText)
    {
        if (Interlocked.Exchange(ref _refreshInProgress, 1) == 1)
        {
            Log.Debug("Monitor refresh request ignored because a refresh is already in progress");
            return;
        }

        Task.Run(() =>
        {
            try
            {
                engine.RefreshMonitors();

                Dispatcher.UIThread.Post(() =>
                {
                    _quickVm?.Refresh();
                    _settingsWindow?.RefreshMonitors(statusText);
                });
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInProgress, 0);
            }
        });
    }
}