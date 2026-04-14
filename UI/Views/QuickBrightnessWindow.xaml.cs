using System.Windows;

namespace BrightSync.UI.Views;

public partial class QuickBrightnessWindow : Window
{
    public QuickBrightnessWindow()
    {
        InitializeComponent();
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
