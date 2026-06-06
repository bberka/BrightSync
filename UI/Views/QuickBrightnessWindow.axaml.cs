using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace BrightSync.UI.Views;

public partial class QuickBrightnessWindow : Window
{
    public QuickBrightnessWindow()
    {
        InitializeComponent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
