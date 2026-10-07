using Avalonia;
using BrightSync.UI;

namespace BrightSync.Tests;

public sealed class TaskbarAwareWindowPositionTests
{
    private static readonly PixelRect WorkingArea = new(0, 0, 1920, 1080);
    private static readonly PixelRect ScreenBounds = new(0, 0, 1920, 1080);

    [Fact]
    public void GetEdge_detects_top_taskbar_from_taskbar_bounds()
    {
        var edge = TaskbarPosition.GetEdge(ScreenBounds, new PixelRect(0, 0, 1920, 48));

        Assert.Equal(TaskbarEdge.Top, edge);
    }

    [Fact]
    public void GetEdge_detects_bottom_taskbar_from_taskbar_bounds()
    {
        var edge = TaskbarPosition.GetEdge(ScreenBounds, new PixelRect(0, 1032, 1920, 48));

        Assert.Equal(TaskbarEdge.Bottom, edge);
    }

    [Fact]
    public void GetEdge_detects_left_taskbar_from_taskbar_bounds()
    {
        var edge = TaskbarPosition.GetEdge(ScreenBounds, new PixelRect(0, 0, 80, 1080));

        Assert.Equal(TaskbarEdge.Left, edge);
    }

    [Fact]
    public void GetEdge_detects_right_taskbar_from_taskbar_bounds()
    {
        var edge = TaskbarPosition.GetEdge(ScreenBounds, new PixelRect(1840, 0, 80, 1080));

        Assert.Equal(TaskbarEdge.Right, edge);
    }

    [Fact]
    public void Calculate_places_popup_above_bottom_taskbar()
    {
        var position = TaskbarAwareWindowPositionCalculator.Calculate(
            WorkingArea,
            scaling: 1,
            width: 300,
            height: 200,
            TaskbarEdge.Bottom);

        Assert.Equal(new PixelPoint(1608, 868), position);
    }

    [Fact]
    public void Calculate_places_popup_below_top_taskbar()
    {
        var position = TaskbarAwareWindowPositionCalculator.Calculate(
            WorkingArea,
            scaling: 1,
            width: 300,
            height: 200,
            TaskbarEdge.Top);

        Assert.Equal(new PixelPoint(1608, 12), position);
    }

    [Fact]
    public void Calculate_places_popup_right_of_left_taskbar()
    {
        var position = TaskbarAwareWindowPositionCalculator.Calculate(
            WorkingArea,
            scaling: 1,
            width: 300,
            height: 200,
            TaskbarEdge.Left);

        Assert.Equal(new PixelPoint(12, 868), position);
    }

    [Fact]
    public void Calculate_places_popup_left_of_right_taskbar()
    {
        var position = TaskbarAwareWindowPositionCalculator.Calculate(
            WorkingArea,
            scaling: 1,
            width: 300,
            height: 200,
            TaskbarEdge.Right);

        Assert.Equal(new PixelPoint(1608, 868), position);
    }

    [Fact]
    public void Calculate_places_full_settings_window_below_top_taskbar()
    {
        var position = TaskbarAwareWindowPositionCalculator.Calculate(
            WorkingArea,
            scaling: 1,
            width: 560,
            height: 770,
            TaskbarEdge.Top);

        Assert.Equal(new PixelPoint(1348, 12), position);
    }
}
