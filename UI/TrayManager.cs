using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using WpfApp = System.Windows.Application;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using BrightSync.Core.Updates;
using BrightSync.UI.ViewModels;
using BrightSync.UI.Views;
using Microsoft.Win32;
using Serilog;

namespace BrightSync.UI;

/// <summary>
/// Manages the system tray icon, context menu, quick brightness popup, and the settings window.
/// Left-click toggles the quick brightness popup; right-click shows the context menu.
/// </summary>
public sealed class TrayManager(
    BrightSyncEngine engine,
    AutoBrightnessService autoBrightness,
    IdleReductionService idleReduction,
    ConfigManager config,
    DdcCiService ddc,
    UpdateChecker updateChecker)
    : IDisposable
{
    public event EventHandler? ExitRequested;

    private NotifyIcon? _notifyIcon;
    private SettingsWindow? _settingsWindow;
    private QuickBrightnessWindow? _quickPopup;
    private QuickBrightnessViewModel? _quickVm;
    private DateTime _lastPopupClosed = DateTime.MinValue;
    private BalloonTipAction _currentBalloonTipAction = BalloonTipAction.None;
    private bool _disposed;
    private int _refreshInProgress;
    private bool? _lastTaskbarUsesLightTheme;

    public void Initialize()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = BuildIcon(taskbarUsesLightTheme: GetTaskbarUsesLightTheme()),
            Text = "BrightSync \u2014 Running",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh Monitors", null, (_, _) => RefreshMonitors());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                Log.Debug("Tray icon left click received");
                ToggleQuickPopup();
            }
        };
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            if (_currentBalloonTipAction == BalloonTipAction.OpenSettings)
                ShowSettings();
        };
        _notifyIcon.BalloonTipClosed += (_, _) => _currentBalloonTipAction = BalloonTipAction.None;

        engine.InternalBrightnessChanged += (_, b) =>
        {
            if (_notifyIcon != null)
                _notifyIcon.Text = $"BrightSync \u2014 Internal: {b}%";
        };
        autoBrightness.BrightnessCorrectionApplied += OnAutoBrightnessCorrectionApplied;
        autoBrightness.DisabledAfterManualBrightnessChange += OnAutoBrightnessDisabledAfterManualChange;
    }

    public void RefreshTrayIconAppearance()
    {
        var notifyIcon = _notifyIcon;
        if (notifyIcon == null)
            return;

        var taskbarUsesLightTheme = GetTaskbarUsesLightTheme();
        if (_lastTaskbarUsesLightTheme == taskbarUsesLightTheme)
            return;

        var previousIcon = notifyIcon.Icon;
        notifyIcon.Icon = BuildIcon(taskbarUsesLightTheme);
        previousIcon?.Dispose();
        Log.Information("Updated tray icon for {TaskbarTheme} taskbar", taskbarUsesLightTheme ? "light" : "dark");
    }

    /// <summary>Opens the full settings window (used by context menu and quick popup).</summary>
    public void ShowSettings()
    {
        Log.Information("Opening settings window");
        WpfApp.Current.Dispatcher.Invoke(() =>
        {
                _quickPopup?.Hide();

            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow(engine, autoBrightness, idleReduction, config, ddc, updateChecker);
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
        WpfApp.Current.Dispatcher.Invoke(() =>
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
        if (_quickPopup == null)
        {
            _quickVm = new QuickBrightnessViewModel(engine, autoBrightness, ddc, config, ShowSettings);
            _quickPopup = new QuickBrightnessWindow { DataContext = _quickVm };
            _quickPopup.Deactivated += (s, e) => _quickPopup.Hide(); // Hide when clicking away
            _quickPopup.SizeChanged += (_, _) =>
            {
                if (_quickPopup.IsVisible)
                    PositionWindowAboveTray(_quickPopup);
            };
            _quickPopup.IsVisibleChanged += (_, _) =>
            {
                if (_quickPopup is { IsVisible: false })
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
        var desktopWorkingArea = SystemParameters.WorkArea;
        double padding = 12;
        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

        window.Left = desktopWorkingArea.Right - width - padding;
        window.Top = desktopWorkingArea.Bottom - height - padding;
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

                WpfApp.Current.Dispatcher.Invoke(() =>
                {
                    _quickVm?.Refresh();
                    _settingsWindow?.RefreshMonitors(statusText);
                });

                if (showBalloonTip && _notifyIcon != null)
                {
                    _currentBalloonTipAction = BalloonTipAction.None;
                    _notifyIcon.ShowBalloonTip(2000, "BrightSync",
                        $"Found {ddc.GetMonitors().Count(m => m.SupportsDdcCi)} brightness-controllable monitor(s).",
                        ToolTipIcon.Info);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInProgress, 0);
            }
        });
    }

    private void OnAutoBrightnessCorrectionApplied(object? sender, AutoBrightnessCorrectionEventArgs e)
    {
        WpfApp.Current.Dispatcher.Invoke(() =>
        {
            var notifyIcon = _notifyIcon;
            if (notifyIcon == null)
                return;

            _currentBalloonTipAction = BalloonTipAction.OpenSettings;
            notifyIcon.ShowBalloonTip(
                5000,
                "BrightSync corrected brightness",
                $"Windows brightness was changed to {e.RequestedBrightness}%, so BrightSync restored automatic brightness to {e.RestoredBrightness}%. If you meant to reduce it, disable automatic brightness manually.",
                ToolTipIcon.Info);
        });
    }

    private void OnAutoBrightnessDisabledAfterManualChange(object? sender, AutoBrightnessDisabledEventArgs e)
    {
        WpfApp.Current.Dispatcher.Invoke(() =>
        {
            var notifyIcon = _notifyIcon;
            if (notifyIcon == null)
                return;

            _currentBalloonTipAction = BalloonTipAction.OpenSettings;
            notifyIcon.ShowBalloonTip(
                5000,
                "Automatic brightness disabled",
                $"Windows brightness was changed manually to {e.RequestedBrightness}%, so BrightSync disabled automatic brightness. Enable lock if this happens often and was unintended.",
                ToolTipIcon.Info);
        });
    }

    /// <summary>Builds a small sun icon with contrast tuned for the current taskbar theme.</summary>
    private Icon BuildIcon(bool taskbarUsesLightTheme)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var foreground = taskbarUsesLightTheme ? Color.FromArgb(24, 24, 24) : Color.White;
        using var pen = new Pen(foreground, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var fill = new SolidBrush(foreground);
        float cx = 8f, cy = 8f;

        // Filled center improves readability at 16x16 while keeping the icon recognizable.
        g.FillEllipse(fill, cx - 2.6f, cy - 2.6f, 5.2f, 5.2f);
        g.DrawEllipse(pen, cx - 3.2f, cy - 3.2f, 6.4f, 6.4f);

        // 8 rays
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4.0;
            g.DrawLine(pen,
                cx + (float)(4.7 * Math.Cos(angle)), cy + (float)(4.7 * Math.Sin(angle)),
                cx + (float)(7.0 * Math.Cos(angle)), cy + (float)(7.0 * Math.Sin(angle)));
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            _lastTaskbarUsesLightTheme = taskbarUsesLightTheme;
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static bool GetTaskbarUsesLightTheme()
    {
        const string personalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const string valueName = "SystemUsesLightTheme";

        using var key = Registry.CurrentUser.OpenSubKey(personalizeKey);
        if (key?.GetValue(valueName) is int themeValue)
            return themeValue != 0;

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Log.Debug("Disposing tray manager");
        autoBrightness.BrightnessCorrectionApplied -= OnAutoBrightnessCorrectionApplied;
        autoBrightness.DisabledAfterManualBrightnessChange -= OnAutoBrightnessDisabledAfterManualChange;
        _quickVm?.Dispose();
        _quickPopup?.Close();
        _notifyIcon?.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private enum BalloonTipAction
    {
        None,
        OpenSettings
    }
}
