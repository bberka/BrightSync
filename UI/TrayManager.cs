using System.Drawing.Drawing2D;
using WpfApp = System.Windows.Application;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using BrightSync.UI.Views;

namespace BrightSync.UI;

/// <summary>
/// Manages the system tray icon, context menu, and the lifecycle of the settings window.
/// </summary>
public sealed class TrayManager(BrightSyncEngine engine, ConfigManager config, DdcCiService ddc)
    : IDisposable
{
    public event EventHandler? ExitRequested;

    private NotifyIcon? _notifyIcon;
    private SettingsWindow? _settingsWindow;
    private bool _disposed;

    public void Initialize()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = BuildIcon(active: true),
            Text = "BrightSync — Running",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh Monitors", null, (_, _) => RefreshMonitors());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();

        // Update tray tooltip when internal brightness changes
        engine.InternalBrightnessChanged += (_, b) =>
        {
            if (_notifyIcon != null)
                _notifyIcon.Text = $"BrightSync — Internal: {b}%";
        };
    }

    private void OpenSettings()
    {
        WpfApp.Current.Dispatcher.Invoke(() =>
        {
            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow(engine, config, ddc);
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

    /// <summary>Creates a simple sun-like icon programmatically — no binary resource needed.</summary>
    private static Icon BuildIcon(bool active)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var fill = active ? Color.FromArgb(255, 255, 200, 0) : Color.FromArgb(180, 180, 180);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(fill, 1.5f);

        // Rays
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4.0;
            float cx = 8f, cy = 8f;
            g.DrawLine(pen,
                cx + (float)(5.5 * Math.Cos(angle)), cy + (float)(5.5 * Math.Sin(angle)),
                cx + (float)(7.0 * Math.Cos(angle)), cy + (float)(7.0 * Math.Sin(angle)));
        }

        // Centre circle
        g.FillEllipse(brush, 3.5f, 3.5f, 9f, 9f);

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon?.Dispose();
    }
}