using System.Drawing.Drawing2D;
using WpfApp = System.Windows.Application;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using BrightSync.UI.ViewModels;
using BrightSync.UI.Views;

namespace BrightSync.UI;

/// <summary>
/// Manages the system tray icon, context menu, quick brightness popup, and the settings window.
/// Left-click toggles the quick brightness popup; right-click shows the context menu.
/// </summary>
public sealed class TrayManager(BrightSyncEngine engine, ConfigManager config, DdcCiService ddc)
    : IDisposable
{
    public event EventHandler? ExitRequested;

    private NotifyIcon? _notifyIcon;
    private SettingsWindow? _settingsWindow;
    private QuickBrightnessWindow? _quickPopup;
    private QuickBrightnessViewModel? _quickVm;
    private DateTime _lastPopupClosed = DateTime.MinValue;
    private bool _disposed;

    public void Initialize()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = BuildIcon(),
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
                ToggleQuickPopup();
        };

        engine.InternalBrightnessChanged += (_, b) =>
        {
            if (_notifyIcon != null)
                _notifyIcon.Text = $"BrightSync \u2014 Internal: {b}%";
        };
    }

    /// <summary>Opens the full settings window (used by context menu and quick popup).</summary>
    public void ShowSettings()
    {
        WpfApp.Current.Dispatcher.Invoke(() =>
        {
            _quickPopup?.Hide();

            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow(engine, config, ddc);
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
                _quickPopup.Hide();
                return;
            }

            // Prevent re-show if just closed by deactivation (tray icon click steals focus)
            if ((DateTime.UtcNow - _lastPopupClosed).TotalMilliseconds < 300)
                return;

            ShowQuickPopup();
        });
    }

    private void ShowQuickPopup()
    {
        if (_quickPopup == null)
        {
            _quickVm = new QuickBrightnessViewModel(engine, ddc, config, ShowSettings);
            _quickPopup = new QuickBrightnessWindow { DataContext = _quickVm };
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

        _quickPopup.Show();
        _quickPopup.Activate();
    }

    private void RefreshMonitors()
    {
        Task.Run(() =>
        {
            engine.RefreshMonitors();
            if (_notifyIcon != null)
                _notifyIcon.ShowBalloonTip(2000, "BrightSync",
                    $"Found {ddc.GetMonitors().Count(m => m.SupportsDdcCi)} DDC/CI monitor(s).",
                    ToolTipIcon.Info);
        });
    }

    /// <summary>Simple monochrome sun outline — white on transparent, no color.</summary>
    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var pen = new Pen(Color.White, 1.2f);
        float cx = 8f, cy = 8f;

        // Circle outline
        g.DrawEllipse(pen, cx - 3f, cy - 3f, 6f, 6f);

        // 8 rays
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4.0;
            g.DrawLine(pen,
                cx + (float)(5.0 * Math.Cos(angle)), cy + (float)(5.0 * Math.Sin(angle)),
                cx + (float)(6.8 * Math.Cos(angle)), cy + (float)(6.8 * Math.Sin(angle)));
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _quickVm?.Dispose();
        _quickPopup?.Close();
        _notifyIcon?.Dispose();
    }
}
