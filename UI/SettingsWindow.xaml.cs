using System.ComponentModel;
using System.Windows;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using Wpf.Ui.Controls;

namespace BrightSync.UI;

public partial class SettingsWindow : FluentWindow
{
    private readonly SettingsWindowViewModel _vm;

    public SettingsWindow(BrightSyncEngine engine, ConfigManager config, DdcCiService ddc)
    {
        InitializeComponent();
        _vm = new SettingsWindowViewModel(engine, config, ddc);
        DataContext = _vm;
        Loaded += (_, _) => PositionBottomRight();
    }

    /// <summary>
    /// Positions the window at the bottom-right of the work area (above the taskbar).
    /// Called after layout so ActualHeight is known.
    /// </summary>
    public void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right  - ActualWidth  - 12;
        Top  = area.Bottom - ActualHeight - 12;
    }

    /// <summary>
    /// Intercepts the close button — hides instead of closing so the window can be
    /// re-shown from the tray icon without recreating it.
    /// The real close (on app exit) goes through Application.Shutdown() which
    /// exits the dispatcher loop regardless of Closing cancellation.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
}
