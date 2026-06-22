using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
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
    private const double ScreenEdgeMargin = 12;
    private const double SettingsWindowPreferredHeight = 770;
    private readonly List<StackPanel> _sectionPanels = new();

    private readonly List<Button> _sidebarButtons = new();

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
        UpdateChecker updateChecker,
        SelfUpdateService selfUpdate)
    {
        InitializeComponent();
        Title = AppVersionInfo.GetDisplayTitle();
        _vm = new SettingsWindowViewModel(engine, autoBrightness, idleReduction, eyeProtection, brightnessBoost, config,
            ddc, updateChecker, selfUpdate);
        DataContext = _vm;

        _vm.AutoBrightnessCurveChanged += OnAutoBrightnessCurveChanged;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        _sidebarButtons.AddRange([
            SidebarGeneralBtn, SidebarBrightnessBtn, SidebarAutoBtn, SidebarSavingBtn,
            SidebarModesBtn, SidebarMonitorsBtn, SidebarAboutBtn
        ]);
        _sectionPanels.AddRange([
            GeneralPanel, BrightnessPanel, AutoPanel, SavingPanel, ModesPanel, MonitorsPanel,
            AboutPanel
        ]);

        UpdateSidebarActiveState();

        Opened += (_, _) =>
        {
            PositionBottomRight(useCursorScreen: true);
            RenderAutoBrightnessCurve();
        };

        SizeChanged += (_, _) =>
        {
            if (IsVisible)
                PositionBottomRight(useCursorScreen: false);
        };

        Closing += (s, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    public event EventHandler? ExitRequested;

    public void PositionBottomRight(bool useCursorScreen = true)
    {
        Screen? screen = null;
        if (useCursorScreen && BrightSync.Core.Interop.NativeMethods.GetCursorPos(out var p))
        {
            screen = Screens.ScreenFromPoint(new PixelPoint(p.x, p.y));
        }

        screen ??= Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen == null) return;

        FitHeightToWorkingArea(screen);

        var workingArea = screen.WorkingArea;
        var scaling = screen.Scaling;

        var windowPhysicalWidth = (int)(FrameSize?.Width ?? (Bounds.Width * scaling));
        var windowPhysicalHeight = (int)(FrameSize?.Height ?? (Bounds.Height * scaling));

        if (windowPhysicalWidth <= 0) windowPhysicalWidth = (int)(Width * scaling);
        if (windowPhysicalHeight <= 0) windowPhysicalHeight = (int)(Height * scaling);

        var margin = (int)(ScreenEdgeMargin * scaling);
        var x = workingArea.Right - windowPhysicalWidth - margin;
        var y = workingArea.Bottom - windowPhysicalHeight - margin;

        Position = new PixelPoint(x, y);
    }

    private void FitHeightToWorkingArea(Screen screen)
    {
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var availableHeight = (screen.WorkingArea.Height / scaling) - (ScreenEdgeMargin * 2);
        var minimumHeight = MinHeight > 0 ? MinHeight : 385;
        Height = Math.Clamp(availableHeight, minimumHeight, SettingsWindowPreferredHeight);
    }

    public void RefreshMonitors(string? statusText = null)
    {
        _vm.RefreshMonitorList(
            string.IsNullOrWhiteSpace(statusText)
                ? null
                : $"{statusText} Found {{0}} monitor(s).");
    }

    public void ShowUpdateAvailable(UpdateCheckResult result)
    {
        _vm.ShowUpdateAvailable(result);
    }

    private void TitleBar_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        BeginMoveDrag(e);
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        ExitOverlay.IsVisible = true;
    }

    private void ExitOverlay_Cancel_Click(object sender, RoutedEventArgs e)
    {
        ExitOverlay.IsVisible = false;
    }

    private void ExitOverlay_Confirm_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateOverlay_Install_Click(object? sender, RoutedEventArgs e)
    {
        _vm.InstallUpdateCommand.Execute(null);
    }

    private void UpdateOverlay_Later_Click(object? sender, RoutedEventArgs e)
    {
        _vm.DismissUpdateCommand.Execute(null);
    }

    private void SidebarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag)
            return;

        if (Enum.TryParse<SettingsSection>(tag, out var section))
            _vm.SelectedSection = section;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsWindowViewModel.SelectedSection))
            UpdateSidebarActiveState();
    }

    private void UpdateSidebarActiveState()
    {
        var sections = Enum.GetValues<SettingsSection>();
        for (var i = 0; i < sections.Length && i < _sidebarButtons.Count && i < _sectionPanels.Count; i++)
        {
            var isActive = sections[i] == _vm.SelectedSection;
            _sidebarButtons[i].Background = isActive
                ? new SolidColorBrush(Color.FromArgb(30, 0, 120, 212))
                : Brushes.Transparent;
            _sidebarButtons[i].Foreground = isActive
                ? SolidColorBrush.Parse("#0078d4")
                : Brushes.White;
            _sectionPanels[i].IsVisible = isActive;
        }

        if (_vm.SelectedSection == SettingsSection.Auto)
        {
            Dispatcher.UIThread.InvokeAsync(RenderAutoBrightnessCurve, DispatcherPriority.Background);
        }
    }

    private void MonitorHeader_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: MonitorRowViewModel vm } && vm.CanExpand)
            vm.IsExpanded = !vm.IsExpanded;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.AutoBrightnessCurveChanged -= OnAutoBrightnessCurveChanged;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.Dispose();
        base.OnClosed(e);
    }

    private void OnAutoBrightnessCurveChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(RenderAutoBrightnessCurve);
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
        var curvePoints = new AvaloniaList<Point>();
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
            StrokeDashArray = new AvaloniaList<double> { 4, 3 },
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

            var x = MinuteToCanvasX(point.MinuteOfDay >= 1440 ? 1440 : point.MinuteOfDay, width) -
                    (ellipse.Width / 2.0);
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
        var left = Math.Clamp(x + 10, 0,
            Math.Max(0, AutoBrightnessCurveCanvas.Bounds.Width - desired.Width));
        var top = Math.Clamp(y - desired.Height - 10, 0,
            Math.Max(0, AutoBrightnessCurveCanvas.Bounds.Height - desired.Height));
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