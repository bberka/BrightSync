using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrightSync.Core;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using BrightSync.Core.Updates;
using BrightSync.UI.ViewModels;

namespace BrightSync.UI.Views;

public partial class SettingsWindow : Window
{
    public event EventHandler? ExitRequested;

    private readonly SettingsWindowViewModel _vm;
    private int _draggingCurvePointIndex = -1;

    public SettingsWindow(
        BrightSyncEngine engine,
        AutoBrightnessService autoBrightness,
        ConfigManager config,
        DdcCiService ddc,
        UpdateChecker updateChecker)
    {
        InitializeComponent();
        Title = AppVersionInfo.GetDisplayTitle();
        _vm = new SettingsWindowViewModel(engine, autoBrightness, config, ddc, updateChecker);
        DataContext = _vm;
        _vm.AutoBrightnessCurveChanged += OnAutoBrightnessCurveChanged;
        Loaded += (_, _) =>
        {
            PositionBottomRight();
            RenderAutoBrightnessCurve();
        };
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

    public void RefreshMonitors(string? statusText = null)
    {
        _vm.RefreshMonitorList(
            string.IsNullOrWhiteSpace(statusText)
                ? null
                : $"{statusText} Found {{0}} monitor(s).");
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
        _vm.AutoBrightnessCurveChanged -= OnAutoBrightnessCurveChanged;
        _vm.Dispose();
        base.OnClosed(e);
    }

    private void OnAutoBrightnessCurveChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(RenderAutoBrightnessCurve);
    }

    private void AutoBrightnessCurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderAutoBrightnessCurve();
    }

    private void AutoBrightnessCurveCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_draggingCurvePointIndex < 0 || e.LeftButton != MouseButtonState.Pressed)
            return;

        var position = e.GetPosition(AutoBrightnessCurveCanvas);
        var brightness = (int)CanvasYToBrightness(position.Y);
        _vm.UpdateAutoBrightnessPoint(_draggingCurvePointIndex, brightness);
    }

    private void AutoBrightnessCurveCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingCurvePointIndex < 0)
            return;

        _draggingCurvePointIndex = -1;
        AutoBrightnessCurveCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void CurveHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not int index)
            return;

        _draggingCurvePointIndex = index;
        AutoBrightnessCurveCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void RenderAutoBrightnessCurve()
    {
        if (!IsLoaded || AutoBrightnessCurveCanvas.ActualWidth <= 1 || AutoBrightnessCurveCanvas.ActualHeight <= 1)
            return;

        AutoBrightnessCurveCanvas.Children.Clear();

        var width = AutoBrightnessCurveCanvas.ActualWidth;
        var height = AutoBrightnessCurveCanvas.ActualHeight;
        var points = _vm.AutoBrightnessCurvePoints;
        if (points.Count == 0)
            return;

        for (var i = 1; i < 4; i++)
        {
            var y = height * i / 4.0;
            AutoBrightnessCurveCanvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 128, 128, 128)),
                StrokeThickness = 1
            });
        }

        var curve = new Polyline
        {
            Stroke = (System.Windows.Media.Brush)FindResource("AccentTextFillColorPrimaryBrush"),
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round
        };

        const int samples = 240;
        for (var sample = 0; sample <= samples; sample++)
        {
            var minute = 1440.0 * sample / samples;
            if (sample == samples)
                minute = 1439.999;

            var brightness = AutoBrightnessCurveEvaluator.Evaluate(points, TimeSpan.FromMinutes(minute));
            curve.Points.Add(new System.Windows.Point(MinuteToCanvasX(minute, width), BrightnessToCanvasY(brightness, height)));
        }

        AutoBrightnessCurveCanvas.Children.Add(curve);

        var nowMinute = DateTime.Now.TimeOfDay.TotalMinutes;
        var nowLine = new Line
        {
            X1 = MinuteToCanvasX(nowMinute, width),
            X2 = MinuteToCanvasX(nowMinute, width),
            Y1 = 0,
            Y2 = height,
            Stroke = (System.Windows.Media.Brush)FindResource("AccentTextFillColorPrimaryBrush"),
            StrokeThickness = 1,
            StrokeDashArray = [4, 3],
            Opacity = 0.6
        };
        AutoBrightnessCurveCanvas.Children.Add(nowLine);

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var ellipse = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = (System.Windows.Media.Brush)FindResource("ApplicationBackgroundBrush"),
                Stroke = (System.Windows.Media.Brush)FindResource("AccentTextFillColorPrimaryBrush"),
                StrokeThickness = 2,
                Cursor = System.Windows.Input.Cursors.SizeNS,
                Tag = i
            };

            ellipse.MouseLeftButtonDown += CurveHandle_MouseLeftButtonDown;

            var x = MinuteToCanvasX(point.MinuteOfDay >= 1440 ? 1440 : point.MinuteOfDay, width) - (ellipse.Width / 2.0);
            var y = BrightnessToCanvasY(point.Brightness, height) - (ellipse.Height / 2.0);
            Canvas.SetLeft(ellipse, x);
            Canvas.SetTop(ellipse, y);
            AutoBrightnessCurveCanvas.Children.Add(ellipse);
        }
    }

    private static double MinuteToCanvasX(double minute, double width)
    {
        var clamped = Math.Clamp(minute, 0, 1440);
        return width * (clamped / 1440.0);
    }

    private static double BrightnessToCanvasY(double brightness, double height)
    {
        var clamped = Math.Clamp(brightness, 0, 100);
        return height * (1.0 - (clamped / 100.0));
    }

    private double CanvasYToBrightness(double y)
    {
        var height = Math.Max(1, AutoBrightnessCurveCanvas.ActualHeight);
        var clampedY = Math.Clamp(y, 0, height);
        return Math.Round((1.0 - (clampedY / height)) * 100.0);
    }
}
