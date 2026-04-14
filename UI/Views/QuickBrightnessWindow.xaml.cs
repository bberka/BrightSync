using System.Windows;

namespace BrightSync.UI.Views;

public partial class QuickBrightnessWindow : Window
{
    public QuickBrightnessWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionNearTray();
    }

    private void PositionNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 16;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Hide();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
