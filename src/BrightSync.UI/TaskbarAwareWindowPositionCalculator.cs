using Avalonia;
using BrightSync.Platform;

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
    /// <summary>
    /// Locates the taskbar. Windows reports it directly; when the OS cannot (Linux), the panel is inferred
    /// from the gap between the screen bounds and its work area.
    /// </summary>
    public static TaskbarPositionInfo GetPosition(PixelRect screenBounds, PixelRect workingArea)
    {
        return PlatformServices.Current.Shell.TryGetTaskbarBounds(out _)
            ? GetPosition(screenBounds)
            : InferFromWorkingArea(screenBounds, workingArea);
    }

    internal static TaskbarPositionInfo InferFromWorkingArea(PixelRect screenBounds, PixelRect workingArea)
    {
        var left = workingArea.X - screenBounds.X;
        var top = workingArea.Y - screenBounds.Y;
        var right = screenBounds.Right - workingArea.Right;
        var bottom = screenBounds.Bottom - workingArea.Bottom;

        var largest = Math.Max(Math.Max(left, top), Math.Max(right, bottom));
        if (largest <= 0)
            return new(TaskbarEdge.Bottom, null);

        if (largest == bottom)
            return new(TaskbarEdge.Bottom, new PixelRect(screenBounds.X, workingArea.Bottom, screenBounds.Width, bottom));
        if (largest == top)
            return new(TaskbarEdge.Top, new PixelRect(screenBounds.X, screenBounds.Y, screenBounds.Width, top));
        if (largest == left)
            return new(TaskbarEdge.Left, new PixelRect(screenBounds.X, screenBounds.Y, left, screenBounds.Height));

        return new(TaskbarEdge.Right, new PixelRect(workingArea.Right, screenBounds.Y, right, screenBounds.Height));
    }

    public static TaskbarPositionInfo GetPosition(PixelRect screenBounds)
    {
        if (!PlatformServices.Current.Shell.TryGetTaskbarBounds(out var rect))
            return new(TaskbarEdge.Bottom, null);

        var taskbarBounds = new PixelRect(
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);

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
