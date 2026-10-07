using System.Runtime.InteropServices;
using Avalonia;
using BrightSync.Core.Interop;

namespace BrightSync.UI;

internal enum TaskbarEdge
{
    Left,
    Top,
    Right,
    Bottom
}

internal static class TaskbarPosition
{
    public static TaskbarEdge GetEdge(PixelRect screenBounds)
    {
        var appBarData = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>()
        };

        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref appBarData) == UIntPtr.Zero)
            return TaskbarEdge.Bottom;

        return GetEdge(
            screenBounds,
            new PixelRect(
                appBarData.rc.Left,
                appBarData.rc.Top,
                appBarData.rc.Right - appBarData.rc.Left,
                appBarData.rc.Bottom - appBarData.rc.Top));
    }

    internal static TaskbarEdge GetEdge(PixelRect screenBounds, PixelRect taskbarBounds)
    {
        var overlapsScreen = taskbarBounds.Right > screenBounds.Position.X &&
                             taskbarBounds.Position.X < screenBounds.Right &&
                             taskbarBounds.Bottom > screenBounds.Position.Y &&
                             taskbarBounds.Position.Y < screenBounds.Bottom;
        if (!overlapsScreen)
            return TaskbarEdge.Bottom;

        var touchesLeft = taskbarBounds.Position.X <= screenBounds.Position.X;
        var touchesTop = taskbarBounds.Position.Y <= screenBounds.Position.Y;
        var touchesRight = taskbarBounds.Right >= screenBounds.Right;
        var touchesBottom = taskbarBounds.Bottom >= screenBounds.Bottom;

        if (touchesLeft && touchesRight)
            return touchesTop ? TaskbarEdge.Top : TaskbarEdge.Bottom;

        if (touchesTop && touchesBottom)
            return touchesLeft ? TaskbarEdge.Left : TaskbarEdge.Right;

        return TaskbarEdge.Bottom;
    }
}

internal static class TaskbarAwareWindowPositionCalculator
{
    private const double EdgeMargin = 12;

    public static PixelPoint Calculate(
        PixelRect workingArea,
        double scaling,
        double width,
        double height,
        TaskbarEdge taskbarEdge)
    {
        if (double.IsNaN(scaling) || scaling <= 0)
            scaling = 1;

        if (double.IsNaN(width) || width <= 0)
            width = 360;
        if (double.IsNaN(height) || height <= 0)
            height = 150;

        var windowPhysicalWidth = (int)(width * scaling);
        var windowPhysicalHeight = (int)(height * scaling);
        var margin = (int)(EdgeMargin * scaling);

        var x = taskbarEdge == TaskbarEdge.Left
            ? workingArea.Position.X + margin
            : workingArea.Right - windowPhysicalWidth - margin;
        var y = taskbarEdge == TaskbarEdge.Top
            ? workingArea.Position.Y + margin
            : workingArea.Bottom - windowPhysicalHeight - margin;

        return new PixelPoint(x, y);
    }
}
