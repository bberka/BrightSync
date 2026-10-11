using System.Runtime.InteropServices;
using BrightSync.Platform;

namespace BrightSync.Core.Services;

internal sealed class WindowsIdleTimeSource : IIdleTimeSource
{
    public TimeSpan GetIdleTime()
    {
        var info = new LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        var currentTick = unchecked((uint)Environment.TickCount64);
        var idleMilliseconds = unchecked(currentTick - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }
}
