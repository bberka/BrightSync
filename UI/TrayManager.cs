using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
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
    public event EventHandler? ExitRequested;

    private TrayIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private QuickBrightnessWindow? _quickPopup;
    private QuickBrightnessViewModel? _quickVm;
    private DateTime _lastPopupClosed = DateTime.MinValue;
    private bool _disposed;
    private int _refreshInProgress;

    public void Initialize()
    {
        var trayIcons = TrayIcon.GetIcons(Application.Current!);
        _trayIcon = trayIcons?.FirstOrDefault();
        if (_trayIcon == null)
        {
            Log.Warning("Tray icon not found in App.axaml, initializing programmatically");
            var assets = Avalonia.Platform.AssetLoader.Open(new Uri("avares://BrightSync/Resources/app.ico"));
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(assets),
                ToolTipText = "BrightSync \u2014 Running",
                IsVisible = true
            };
            if (trayIcons == null)
            {
                trayIcons = new TrayIcons();
                TrayIcon.SetIcons(Application.Current!, trayIcons);
            }
            trayIcons.Add(_trayIcon);
        }
        else
        {
            Log.Information("Tray icon resolved from App.axaml");
            _trayIcon.IsVisible = true;
        }

        RefreshTrayMenu();

        _trayIcon.Clicked += (_, _) =>
        {
            Log.Debug("Tray icon click received");
            ToggleQuickPopup();
        };

        engine.MasterBrightnessChanged += (_, b) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_trayIcon != null)
                    _trayIcon.ToolTipText = $"BrightSync \u2014 Master: {b}%";
            });
        };

        eyeProtection.StateChanged += (_, _) => RefreshTrayMenu();
        brightnessBoost.StateChanged += (_, _) => RefreshTrayMenu();
    }

    private void RefreshTrayMenu()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon == null) return;

            var menu = new NativeMenu();

            var settingsItem = new NativeMenuItem("Settings");
            settingsItem.Click += (_, _) => ShowSettings();
            menu.Items.Add(settingsItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            // Eye Protection
            var eyeItem = new NativeMenuItem("Eye Protection")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = eyeProtection.IsEnabled
            };
            eyeItem.Click += (_, _) => eyeProtection.SetEnabled(!eyeProtection.IsEnabled);

            var eyeSubmenu = new NativeMenu();
            var eyePresets = new[] { 1, 2, 3, 4, 8, 12, 24 };
            foreach (var hours in eyePresets)
            {
                var label = hours == 1 ? "1 hour" : $"{hours} hours";
                var presetItem = new NativeMenuItem(label);
                presetItem.Click += (_, _) => eyeProtection.SetEnabled(true, hours);
                eyeSubmenu.Items.Add(presetItem);
            }
            eyeItem.Menu = eyeSubmenu;
            menu.Items.Add(eyeItem);

            // Brightness Boost
            var boostItem = new NativeMenuItem("Brightness Boost")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = brightnessBoost.IsEnabled
            };
            boostItem.Click += (_, _) => brightnessBoost.SetEnabled(!brightnessBoost.IsEnabled);

            var boostSubmenu = new NativeMenu();
            var boostPresets = new[] { 1, 2, 3, 4, 8, 12, 24 };
            foreach (var hours in boostPresets)
            {
                var label = hours == 1 ? "1 hour" : $"{hours} hours";
                var presetItem = new NativeMenuItem(label);
                presetItem.Click += (_, _) => brightnessBoost.SetEnabled(true, hours);
                boostSubmenu.Items.Add(presetItem);
            }
            boostItem.Menu = boostSubmenu;
            menu.Items.Add(boostItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            var refreshItem = new NativeMenuItem("Refresh Monitors");
            refreshItem.Click += (_, _) => RefreshMonitors();
            menu.Items.Add(refreshItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("Exit");
            exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
            menu.Items.Add(exitItem);

            _trayIcon.Menu = menu;
        });
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
                _settingsWindow = new SettingsWindow(engine, autoBrightness, idleReduction, eyeProtection, brightnessBoost, config, ddc, updateChecker);
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
            _quickVm = new QuickBrightnessViewModel(engine, autoBrightness, eyeProtection, brightnessBoost, ddc, config, ShowSettings);
            _quickPopup = new QuickBrightnessWindow { DataContext = _quickVm };
            _quickPopup.Deactivated += (s, e) => _quickPopup.Hide(); // Hide when clicking away
            
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
        _quickPopup.Show();
        _quickPopup.UpdateLayout();
        PositionWindowAboveTray(_quickPopup);
        _quickPopup.Opacity = 1;
        _quickPopup.Activate();
        Log.Debug("Quick brightness popup shown");
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

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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
        _quickVm?.Dispose();
        _quickPopup?.Close();
        
        if (_trayIcon != null)
        {
            var trayIcons = TrayIcon.GetIcons(Application.Current!);
            trayIcons?.Remove(_trayIcon);
            _trayIcon.Dispose();
        }
    }
}
