using Avalonia;
using BrightSync.UI;

namespace BrightSync.Tests;

public sealed class QuickPopupPositionTests
{
    private static readonly PixelRect WorkingArea = new(0, 0, 1920, 1080);

    [Fact]
    public void Calculate_places_popup_above_bottom_taskbar()
    {
        var position = QuickPopupPositionCalculator.Calculate(
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
        var position = QuickPopupPositionCalculator.Calculate(
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
        var position = QuickPopupPositionCalculator.Calculate(
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
        var position = QuickPopupPositionCalculator.Calculate(
            WorkingArea,
            scaling: 1,
            width: 300,
            height: 200,
            TaskbarEdge.Right);

        Assert.Equal(new PixelPoint(1608, 868), position);
    }
}
