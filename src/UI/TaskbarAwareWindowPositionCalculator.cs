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

internal readonly record struct TaskbarPositionInfo(TaskbarEdge Edge, PixelRect? Bounds);

internal static class TaskbarPosition
{
    public static TaskbarPositionInfo GetPosition(PixelRect screenBounds)
    {
        var appBarData = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>()
        };

        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref appBarData) == UIntPtr.Zero)
            return new(TaskbarEdge.Bottom, null);

        var taskbarBounds = new PixelRect(
            appBarData.rc.Left,
            appBarData.rc.Top,
            appBarData.rc.Right - appBarData.rc.Left,
            appBarData.rc.Bottom - appBarData.rc.Top);

        return new(
            GetEdge(screenBounds, taskbarBounds),
            OverlapsScreen(screenBounds, taskbarBounds) ? taskbarBounds : null);
    }

    internal static TaskbarEdge GetEdge(PixelRect screenBounds, PixelRect taskbarBounds)
    {
        var overlapsScreen = OverlapsScreen(screenBounds, taskbarBounds);
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

    private static bool OverlapsScreen(PixelRect screenBounds, PixelRect taskbarBounds)
    {
        return taskbarBounds.Right > screenBounds.Position.X &&
               taskbarBounds.Position.X < screenBounds.Right &&
               taskbarBounds.Bottom > screenBounds.Position.Y &&
               taskbarBounds.Position.Y < screenBounds.Bottom;
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
        return Calculate(
            workingArea,
            workingArea,
            scaling,
            width,
            height,
            taskbarEdge,
            taskbarBounds: null);
    }

    public static PixelPoint Calculate(
        PixelRect screenBounds,
        PixelRect workingArea,
        double scaling,
        double width,
        double height,
        TaskbarEdge taskbarEdge,
        PixelRect? taskbarBounds)
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

        var x = taskbarBounds is { } currentTaskbarBounds
            ? taskbarEdge switch
            {
                TaskbarEdge.Left => currentTaskbarBounds.Right + margin,
                TaskbarEdge.Right => currentTaskbarBounds.Position.X - windowPhysicalWidth - margin,
                _ => screenBounds.Right - windowPhysicalWidth - margin
            }
            : taskbarEdge == TaskbarEdge.Left
                ? workingArea.Position.X + margin
                : workingArea.Right - windowPhysicalWidth - margin;

        var y = taskbarBounds is { } currentTaskbarBoundsForY
            ? taskbarEdge switch
            {
                TaskbarEdge.Top => currentTaskbarBoundsForY.Bottom + margin,
                TaskbarEdge.Bottom => currentTaskbarBoundsForY.Position.Y - windowPhysicalHeight - margin,
                _ => screenBounds.Bottom - windowPhysicalHeight - margin
            }
            : taskbarEdge == TaskbarEdge.Top
                ? workingArea.Position.Y + margin
                : workingArea.Bottom - windowPhysicalHeight - margin;

        return new PixelPoint(x, y);
    }
}
