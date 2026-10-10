using System.Runtime.InteropServices;

namespace BrightSync.Platform.Linux.Services;

/// <summary>
/// Optional X11 helpers (pointer position, XScreenSaver idle time). Works on Xorg and XWayland;
/// every call degrades to "unavailable" when libX11 / libXss or a display is missing.
/// </summary>
internal static unsafe partial class X11Native
{
    private static readonly object Gate = new();
    private static nint _display;
    private static bool _opened;

    public static bool TryGetCursorPosition(out int x, out int y)
    {
        x = 0;
        y = 0;
        try
        {
            lock (Gate)
            {
                var display = GetDisplay();
                if (display == 0)
                    return false;

                var root = XDefaultRootWindow(display);
                if (XQueryPointer(display, root, out _, out _, out x, out y, out _, out _, out _) == 0)
                    return false;

                return true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static bool TryGetIdleTime(out TimeSpan idle)
    {
        idle = TimeSpan.Zero;
        try
        {
            lock (Gate)
            {
                var display = GetDisplay();
                if (display == 0)
                    return false;

                var info = default(ScreenSaverInfo);
                if (XScreenSaverQueryInfo(display, XDefaultRootWindow(display), &info) == 0)
                    return false;

                idle = TimeSpan.FromMilliseconds(info.Idle);
                return true;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>True when an X display is set and libXss can be loaded, so idle time can be read.</summary>
    public static bool IsIdleQueryAvailable()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
           NativeLibrary.TryLoad("libXss.so.1", out var handle) &&
           Release(handle);

    private static bool Release(nint handle)
    {
        NativeLibrary.Free(handle);
        return true;
    }

    private static nint GetDisplay()
    {
        if (_opened)
            return _display;

        _opened = true;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return 0;

        _display = XOpenDisplay(0);
        return _display;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenSaverInfo
    {
        public nuint Window;
        public int State;
        public int Kind;
        public nuint TilOrSince;
        public nuint Idle;
        public nuint EventMask;
    }

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint displayName);

    [LibraryImport("libX11.so.6")]
    private static partial nuint XDefaultRootWindow(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XQueryPointer(
        nint display,
        nuint window,
        out nuint rootReturn,
        out nuint childReturn,
        out int rootX,
        out int rootY,
        out int windowX,
        out int windowY,
        out uint maskReturn);

    [LibraryImport("libXss.so.1")]
    private static partial int XScreenSaverQueryInfo(nint display, nuint drawable, ScreenSaverInfo* info);
}
