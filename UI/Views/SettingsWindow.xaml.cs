using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using BrightSync.Core;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using BrightSync.UI.ViewModels;

namespace BrightSync.UI.Views;

public partial class SettingsWindow : Window
{
    public event EventHandler? ExitRequested;

    private readonly SettingsWindowViewModel _vm;

    public SettingsWindow(BrightSyncEngine engine, ConfigManager config, DdcCiService ddc)
    {
        InitializeComponent();
        Title = AppVersionInfo.GetDisplayTitle();
        _vm = new SettingsWindowViewModel(engine, config, ddc);
        DataContext = _vm;
        Loaded += (_, _) => PositionBottomRight();
        SizeChanged += (_, _) =>
        {
            if (IsLoaded && IsVisible)
                PositionBottomRight();
        };
    }

    public void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right  - ActualWidth  - 12;
        Top  = area.Bottom - ActualHeight - 12;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        ExitOverlay.Visibility = Visibility.Visible;
    }

    private void ExitOverlay_Cancel_Click(object sender, RoutedEventArgs e)
    {
        ExitOverlay.Visibility = Visibility.Collapsed;
    }

    private void ExitOverlay_Confirm_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MonitorCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MonitorRowViewModel vm })
            vm.IsExpanded = !vm.IsExpanded;
    }

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
