using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
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
    private Border? _dragValueBadge;

    public SettingsWindow()
    {
        InitializeComponent();
        _vm = null!;
    }

    public SettingsWindow(
        BrightSyncEngine engine,
        AutoBrightnessService autoBrightness,
        IdleReductionService idleReduction,
        EyeProtectionService eyeProtection,
        BrightnessBoostService brightnessBoost,
        ConfigManager config,
        DdcCiService ddc,
        UpdateChecker updateChecker)
    {
        InitializeComponent();
        Title = AppVersionInfo.GetDisplayTitle();
        _vm = new SettingsWindowViewModel(engine, autoBrightness, idleReduction, eyeProtection, brightnessBoost, config, ddc, updateChecker);
        DataContext = _vm;
        
        _vm.AutoBrightnessCurveChanged += OnAutoBrightnessCurveChanged;
        
        Opened += (_, _) =>
        {
            PositionBottomRight();
            RenderAutoBrightnessCurve();
        };

        SizeChanged += (_, _) =>
        {
            if (IsVisible)
                PositionBottomRight();
        };

        Closing += (s, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    public void PositionBottomRight()
    {
        var screen = Screens.ScreenFromVisual(this);
        if (screen == null) return;

        var workingArea = screen.WorkingArea;
        var scaling = screen.Scaling;

        var windowPhysicalWidth = (int)(FrameSize?.Width ?? (Bounds.Width * scaling));
        var windowPhysicalHeight = (int)(FrameSize?.Height ?? (Bounds.Height * scaling));

        if (windowPhysicalWidth <= 0) windowPhysicalWidth = (int)(Width * scaling);
        if (windowPhysicalHeight <= 0) windowPhysicalHeight = (int)(Height * scaling);

        var x = workingArea.Right - windowPhysicalWidth - (int)(12 * scaling);
        var y = workingArea.Bottom - windowPhysicalHeight - (int)(12 * scaling);

        Position = new PixelPoint(x, y);
    }

    public void RefreshMonitors(string? statusText = null)
    {
        _vm.RefreshMonitorList(
            string.IsNullOrWhiteSpace(statusText)
                ? null
                : $"{statusText} Found {{0}} monitor(s).");
    }

    private void TitleBar_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    private void MinimizeToTray_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Hide();
    }

    private void Exit_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ExitOverlay.IsVisible = true;
    }

    private void ExitOverlay_Cancel_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ExitOverlay.IsVisible = false;
    }

    private void ExitOverlay_Confirm_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MonitorHeader_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: MonitorRowViewModel vm } && vm.CanExpand)
            vm.IsExpanded = !vm.IsExpanded;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.AutoBrightnessCurveChanged -= OnAutoBrightnessCurveChanged;
        _vm.Dispose();
        base.OnClosed(e);
    }

    private void OnAutoBrightnessCurveChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(RenderAutoBrightnessCurve);
    }

    private void AutoBrightnessCurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderAutoBrightnessCurve();
    }

    private void AutoBrightnessCurveCanvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingCurvePointIndex < 0)
            return;

        var position = e.GetPosition(AutoBrightnessCurveCanvas);
        var brightness = (int)CanvasYToBrightness(position.Y);
        _vm.UpdateAutoBrightnessPoint(_draggingCurvePointIndex, brightness, isDragging: true);
        UpdateDragValueBadge(position.X, position.Y, brightness);
    }

    private void AutoBrightnessCurveCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggingCurvePointIndex < 0)
            return;

        // Finalize setting updates and trigger autosave now that drag is finished
        var point = _vm.AutoBrightnessCurvePoints[_draggingCurvePointIndex];
        _vm.UpdateAutoBrightnessPoint(_draggingCurvePointIndex, point.Brightness, isDragging: false);

        _draggingCurvePointIndex = -1;
        RemoveDragValueBadge();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void CurveHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control element || element.Tag is not int index)
            return;

        var properties = e.GetCurrentPoint(AutoBrightnessCurveCanvas).Properties;
        if (!properties.IsLeftButtonPressed)
            return;

        _draggingCurvePointIndex = index;
        e.Pointer.Capture(AutoBrightnessCurveCanvas);
        var point = _vm.AutoBrightnessCurvePoints[index];
        var pos = e.GetPosition(AutoBrightnessCurveCanvas);
        UpdateDragValueBadge(pos.X, pos.Y, point.Brightness);
        e.Handled = true;
    }

    private void RenderAutoBrightnessCurve()
    {
        var width = AutoBrightnessCurveCanvas.Bounds.Width;
        var height = AutoBrightnessCurveCanvas.Bounds.Height;

        if (width <= 1 || height <= 1)
            return;

        AutoBrightnessCurveCanvas.Children.Clear();
        _dragValueBadge = null;

        var points = _vm.AutoBrightnessCurvePoints;
        if (points.Count == 0)
            return;

        // Draw horizontal grid lines
        for (var i = 1; i < 4; i++)
        {
            var y = height * i / 4.0;
            AutoBrightnessCurveCanvas.Children.Add(new Line
            {
                StartPoint = new Point(0, y),
                EndPoint = new Point(width, y),
                Stroke = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                StrokeThickness = 1
            });
        }

        // Draw polyline curve
        var curvePoints = new Avalonia.Collections.AvaloniaList<Point>();
        const int samples = 240;
        for (var sample = 0; sample <= samples; sample++)
        {
            var minute = 1440.0 * sample / samples;
            if (sample == samples)
                minute = 1439.999;

            var brightness = AutoBrightnessCurveEvaluator.Evaluate(points, TimeSpan.FromMinutes(minute));
            curvePoints.Add(new Point(MinuteToCanvasX(minute, width), BrightnessToCanvasY(brightness, height)));
        }

        var curve = new Polyline
        {
            Stroke = SolidColorBrush.Parse("#0078d4"),
            StrokeThickness = 2.5,
            StrokeJoin = PenLineJoin.Round,
            Points = curvePoints
        };
        AutoBrightnessCurveCanvas.Children.Add(curve);

        // Draw current time vertical line
        var nowMinute = DateTime.Now.TimeOfDay.TotalMinutes;
        var nowLine = new Line
        {
            StartPoint = new Point(MinuteToCanvasX(nowMinute, width), 0),
            EndPoint = new Point(MinuteToCanvasX(nowMinute, width), height),
            Stroke = SolidColorBrush.Parse("#0078d4"),
            StrokeThickness = 1,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 3 },
            Opacity = 0.6
        };
        AutoBrightnessCurveCanvas.Children.Add(nowLine);

        // Draw handle ellipses
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var ellipse = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = SolidColorBrush.Parse("#1e1e1e"),
                Stroke = SolidColorBrush.Parse("#0078d4"),
                StrokeThickness = 2,
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
                Tag = i
            };

            ellipse.PointerPressed += CurveHandle_PointerPressed;

            var x = MinuteToCanvasX(point.MinuteOfDay >= 1440 ? 1440 : point.MinuteOfDay, width) - (ellipse.Width / 2.0);
            var y = BrightnessToCanvasY(point.Brightness, height) - (ellipse.Height / 2.0);
            Canvas.SetLeft(ellipse, x);
            Canvas.SetTop(ellipse, y);
            AutoBrightnessCurveCanvas.Children.Add(ellipse);
        }
    }

    private void UpdateDragValueBadge(double x, double y, int brightness)
    {
        if (_dragValueBadge == null)
        {
            _dragValueBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 32, 32, 32)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3, 6, 3),
                Child = new TextBlock
                {
                    FontSize = 11,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.SemiBold
                },
                IsHitTestVisible = false
            };
            AutoBrightnessCurveCanvas.Children.Add(_dragValueBadge);
        }

        if (_dragValueBadge.Child is TextBlock text)
            text.Text = $"{brightness}%";

        _dragValueBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = _dragValueBadge.DesiredSize;
        var left = Math.Clamp(x + 10, 0, Math.Max(0, AutoBrightnessCurveCanvas.Bounds.Width - desired.Width));
        var top = Math.Clamp(y - desired.Height - 10, 0, Math.Max(0, AutoBrightnessCurveCanvas.Bounds.Height - desired.Height));
        Canvas.SetLeft(_dragValueBadge, left);
        Canvas.SetTop(_dragValueBadge, top);
    }

    private void RemoveDragValueBadge()
    {
        if (_dragValueBadge == null)
            return;

        AutoBrightnessCurveCanvas.Children.Remove(_dragValueBadge);
        _dragValueBadge = null;
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
        var height = Math.Max(1, AutoBrightnessCurveCanvas.Bounds.Height);
        var clampedY = Math.Clamp(y, 0, height);
        return Math.Round((1.0 - (clampedY / height)) * 100.0);
    }
}
