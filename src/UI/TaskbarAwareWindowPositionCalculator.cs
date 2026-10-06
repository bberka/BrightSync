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
    public static TaskbarEdge GetEdge()
    {
        var appBarData = new NativeMethods.APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>()
        };

        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref appBarData) == UIntPtr.Zero)
            return TaskbarEdge.Bottom;

        return appBarData.uEdge switch
        {
            NativeMethods.ABE_LEFT => TaskbarEdge.Left,
            NativeMethods.ABE_TOP => TaskbarEdge.Top,
            NativeMethods.ABE_RIGHT => TaskbarEdge.Right,
            NativeMethods.ABE_BOTTOM => TaskbarEdge.Bottom,
            _ => TaskbarEdge.Bottom
        };
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
