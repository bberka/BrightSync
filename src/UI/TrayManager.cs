using System;
using System.Threading;
using System.Threading.Tasks;
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
/// Owns the system tray icon lifecycle. The <see cref="TrayIcon"/> itself is
/// declared in <c>App.axaml</c> via <c>&lt;TrayIcon.Icons&gt;</c> and bound to a
/// <see cref="TrayMenuViewModel"/>. This manager wires the VM's commands to the
/// existing services and updates the tray icon's tooltip when the master
/// brightness changes. AOT-safe: no reflection, no dynamic, primitives only.
/// </summary>
public sealed class TrayManager : IDisposable
{
    public event EventHandler? ExitRequested;

    private readonly TrayMenuViewModel _vm;
    private readonly TrayIcon _trayIcon;
    private readonly BrightSyncEngine _engine;
    private readonly AutoBrightnessService _autoBrightness;
    private readonly IdleReductionService _idleReduction;
    private readonly EyeProtectionService _eyeProtection;
    private readonly BrightnessBoostService _brightnessBoost;
    private readonly ConfigManager _config;
    private readonly DdcCiService _ddc;
    private readonly UpdateChecker _updateChecker;

    private SettingsWindow? _settingsWindow;
    private QuickBrightnessWindow? _quickPopup;
    private QuickBrightnessViewModel? _quickVm;
    private DateTime _quickPopupShownAt = DateTime.MinValue;
    private DateTime _lastPopupClosed = DateTime.MinValue;
    private bool _disposed;
    private int _refreshInProgress;

    public TrayManager(
        TrayIcon trayIcon,
        BrightSyncEngine engine,
        AutoBrightnessService autoBrightness,
        IdleReductionService idleReduction,
        EyeProtectionService eyeProtection,
        BrightnessBoostService brightnessBoost,
        ConfigManager config,
        DdcCiService ddc,
        UpdateChecker updateChecker)
    {
        _trayIcon = trayIcon ?? throw new ArgumentNullException(nameof(trayIcon));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _autoBrightness = autoBrightness ?? throw new ArgumentNullException(nameof(autoBrightness));
        _idleReduction = idleReduction ?? throw new ArgumentNullException(nameof(idleReduction));
        _eyeProtection = eyeProtection ?? throw new ArgumentNullException(nameof(eyeProtection));
        _brightnessBoost = brightnessBoost ?? throw new ArgumentNullException(nameof(brightnessBoost));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _ddc = ddc ?? throw new ArgumentNullException(nameof(ddc));
        _updateChecker = updateChecker ?? throw new ArgumentNullException(nameof(updateChecker));

        _vm = new TrayMenuViewModel(
            engine,
            eyeProtection,
            brightnessBoost,
            openSettings: ShowSettings,
            exitApp: () => ExitRequested?.Invoke(this, EventArgs.Empty),
            toggleQuickPopup: ToggleQuickPopup,
            refreshMonitors: RefreshMonitors);

        _engine.MasterBrightnessChanged += OnMasterBrightnessChanged;
        UpdateTooltip(_engine.MasterBrightness);
    }

    public TrayMenuViewModel ViewModel => _vm;

    private void OnMasterBrightnessChanged(object? sender, int brightness)
    {
        Dispatcher.UIThread.Post(() => UpdateTooltip(brightness));
    }

    private void UpdateTooltip(int brightness)
    {
        _trayIcon.ToolTipText = $"BrightSync - Master: {brightness}%";
    }

    public void ShowSettings()
    {
        Log.Information("Opening settings window");
        Dispatcher.UIThread.Post(() =>
        {
            _quickPopup?.Hide();

            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(_engine, _autoBrightness, _idleReduction, _eyeProtection, _brightnessBoost, _config, _ddc, _updateChecker);
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
            _quickVm = new QuickBrightnessViewModel(_engine, _autoBrightness, _eyeProtection, _brightnessBoost, _ddc, _config,
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
                _engine.RefreshMonitors();

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Debug("Disposing tray manager");
        _engine.MasterBrightnessChanged -= OnMasterBrightnessChanged;
        _quickVm?.Dispose();
        _quickPopup?.Close();
        _vm.Dispose();
    }
}
