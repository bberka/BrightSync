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

    private async void Exit_Click(object sender, RoutedEventArgs e)
    {
        var messageBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Exit BrightSync",
            Content = "Are you sure you want to exit?\nBrightSync will stop syncing monitor brightness.",
            PrimaryButtonText = "Exit",
            CloseButtonText = "Cancel",
            Topmost = true
        };

        var result = await messageBox.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
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
