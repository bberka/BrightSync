using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Serilog;

namespace BrightSync.UI;

internal sealed class WindowsTrayIcon : IDisposable
{
    private const int IconId = 1;
    private const int CallbackMessage = NativeMethods.WmApp + 42;

    private const int CommandSettings = 1001;
    private const int CommandRefresh = 1002;
    private const int CommandExit = 1003;
    private const int CommandEyeToggle = 1100;
    private const int CommandEyePresetBase = 1110;
    private const int CommandBoostToggle = 1200;
    private const int CommandBoostPresetBase = 1210;

    private static readonly int[] PresetHours = [1, 2, 3, 4, 8, 12, 24];

    private readonly string _windowClassName = $"BrightSyncTrayWindow-{Guid.NewGuid():N}";
    private readonly NativeMethods.WndProc _wndProc;

    private IntPtr _windowHandle;
    private IntPtr _moduleHandle;
    private IntPtr _iconHandle;
    private bool _ownsIconHandle;
    private bool _registered;
    private bool _eyeProtectionEnabled;
    private bool _brightnessBoostEnabled;

    public WindowsTrayIcon()
    {
        _wndProc = WindowProc;
    }

    public event EventHandler? Clicked;
    public event EventHandler? SettingsRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? EyeProtectionToggleRequested;
    public event EventHandler? BrightnessBoostToggleRequested;
    public event EventHandler<int>? EyeProtectionPresetRequested;
    public event EventHandler<int>? BrightnessBoostPresetRequested;

    public void Initialize(string toolTip, bool eyeProtectionEnabled, bool brightnessBoostEnabled)
    {
        _eyeProtectionEnabled = eyeProtectionEnabled;
        _brightnessBoostEnabled = brightnessBoostEnabled;

        _moduleHandle = NativeMethods.GetModuleHandle(null);
        RegisterWindowClass();

        _windowHandle = NativeMethods.CreateWindowEx(
            0,
            _windowClassName,
            "BrightSync Tray Window",
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            _moduleHandle,
            IntPtr.Zero);

        if (_windowHandle == IntPtr.Zero)
            ThrowLastWin32Error("Failed to create tray message window");

        _iconHandle = LoadApplicationIcon();
        AddIcon(toolTip);
        Log.Information("Win32 tray icon initialized. WindowHandle={WindowHandle}, HasIcon={HasIcon}",
            _windowHandle,
            _iconHandle != IntPtr.Zero);
    }

    public void SetToolTip(string toolTip)
    {
        if (_windowHandle == IntPtr.Zero || !_registered)
            return;

        var data = CreateNotifyIconData(toolTip);
        data.uFlags = NativeMethods.NifTip;
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NimModify, ref data))
            Log.Warning("Shell_NotifyIcon(NIM_MODIFY tooltip) failed. LastWin32Error={LastWin32Error}",
                Marshal.GetLastWin32Error());
    }

    public void UpdateMenuState(bool eyeProtectionEnabled, bool brightnessBoostEnabled)
    {
        _eyeProtectionEnabled = eyeProtectionEnabled;
        _brightnessBoostEnabled = brightnessBoostEnabled;
    }

    private void RegisterWindowClass()
    {
        var windowClass = new NativeMethods.WndClassEx
        {
            cbSize = Marshal.SizeOf<NativeMethods.WndClassEx>(),
            lpfnWndProc = _wndProc,
            hInstance = _moduleHandle,
            lpszClassName = _windowClassName
        };

        var atom = NativeMethods.RegisterClassEx(ref windowClass);
        if (atom == 0)
            ThrowLastWin32Error("Failed to register tray window class");
    }

    private IntPtr LoadApplicationIcon()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var largeIcons = new IntPtr[1];
            var smallIcons = new IntPtr[1];
            var count = NativeMethods.ExtractIconEx(path, 0, largeIcons, smallIcons, 1);
            if (count > 0)
            {
                if (largeIcons[0] != IntPtr.Zero)
                    NativeMethods.DestroyIcon(largeIcons[0]);

                if (smallIcons[0] != IntPtr.Zero)
                {
                    _ownsIconHandle = true;
                    return smallIcons[0];
                }
            }
        }

        _ownsIconHandle = false;
        return NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(NativeMethods.IdiApplication));
    }

    private void AddIcon(string toolTip)
    {
        var data = CreateNotifyIconData(toolTip);
        data.uFlags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _iconHandle;

        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref data))
            ThrowLastWin32Error("Shell_NotifyIcon(NIM_ADD) failed");

        _registered = true;
        if (PromoteCurrentExecutableNotificationIcon())
        {
            NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref data);
            _registered = false;

            if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref data))
                ThrowLastWin32Error("Shell_NotifyIcon(NIM_ADD after promotion) failed");

            _registered = true;
        }

        data.uVersion = NativeMethods.NotifyIconVersion4;
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NimSetVersion, ref data))
            Log.Warning("Shell_NotifyIcon(NIM_SETVERSION) failed. LastWin32Error={LastWin32Error}",
                Marshal.GetLastWin32Error());
    }

    private NativeMethods.NotifyIconData CreateNotifyIconData(string toolTip)
    {
        return new NativeMethods.NotifyIconData
        {
            cbSize = Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            hWnd = _windowHandle,
            uID = IconId,
            szTip = CreateFixedString(toolTip, 128),
            szInfo = new char[256],
            szInfoTitle = new char[64]
        };
    }

    private static char[] CreateFixedString(string value, int size)
    {
        var chars = new char[size];
        var length = Math.Min(value.Length, size - 1);
        value.CopyTo(0, chars, 0, length);
        return chars;
    }

    private static bool PromoteCurrentExecutableNotificationIcon()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            return false;

        using var settings = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
        if (settings == null)
            return false;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            foreach (var subKeyName in settings.GetSubKeyNames())
            {
                using var subKey = settings.OpenSubKey(subKeyName, writable: true);
                if (subKey?.GetValue("ExecutablePath") is not string executablePath ||
                    !string.Equals(executablePath, processPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var current = subKey.GetValue("IsPromoted");
                if (current is int promoted && promoted == 1)
                    return false;

                subKey.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                Log.Information("Promoted Windows notification icon for {ExecutablePath}", processPath);
                return true;
            }

            Thread.Sleep(100);
        }

        Log.Debug("Windows notification icon settings entry was not found for {ExecutablePath}", processPath);
        return false;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            var eventCode = unchecked((int)((long)lParam & 0xffff));
            switch (eventCode)
            {
                case NativeMethods.WmLButtonUp:
                case NativeMethods.NinSelect:
                case NativeMethods.NinKeySelect:
                    Log.Debug("Win32 tray icon click received. EventCode={EventCode}", eventCode);
                    Clicked?.Invoke(this, EventArgs.Empty);
                    return IntPtr.Zero;
                case NativeMethods.WmRButtonUp:
                case NativeMethods.WmContextMenu:
                    ShowContextMenu();
                    return IntPtr.Zero;
            }
        }

        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!NativeMethods.GetCursorPos(out var point))
            point = new NativeMethods.Point();

        var menu = NativeMethods.CreatePopupMenu();
        var eyeMenu = NativeMethods.CreatePopupMenu();
        var boostMenu = NativeMethods.CreatePopupMenu();

        try
        {
            AppendCommand(menu, CommandSettings, "Settings");
            AppendSeparator(menu);

            AppendCommand(eyeMenu, CommandEyeToggle, "Toggle Eye Protection",
                _eyeProtectionEnabled ? NativeMethods.MfChecked : 0);
            AppendSeparator(eyeMenu);
            AppendPresetCommands(eyeMenu, CommandEyePresetBase);
            AppendPopup(menu, eyeMenu, "Eye Protection", _eyeProtectionEnabled);

            AppendCommand(boostMenu, CommandBoostToggle, "Toggle Brightness Boost",
                _brightnessBoostEnabled ? NativeMethods.MfChecked : 0);
            AppendSeparator(boostMenu);
            AppendPresetCommands(boostMenu, CommandBoostPresetBase);
            AppendPopup(menu, boostMenu, "Brightness Boost", _brightnessBoostEnabled);

            AppendSeparator(menu);
            AppendCommand(menu, CommandRefresh, "Refresh Monitors");
            AppendSeparator(menu);
            AppendCommand(menu, CommandExit, "Exit");

            NativeMethods.SetForegroundWindow(_windowHandle);
            var command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TpmReturNcmd | NativeMethods.TpmRightButton,
                point.X,
                point.Y,
                _windowHandle,
                IntPtr.Zero);

            DispatchMenuCommand(command);
        }
        finally
        {
            NativeMethods.DestroyMenu(boostMenu);
            NativeMethods.DestroyMenu(eyeMenu);
            NativeMethods.DestroyMenu(menu);
        }
    }

    private static void AppendPresetCommands(IntPtr menu, int commandBase)
    {
        for (var i = 0; i < PresetHours.Length; i++)
        {
            var hours = PresetHours[i];
            AppendCommand(menu, commandBase + i, hours == 1 ? "1 hour" : $"{hours} hours");
        }
    }

    private static void AppendCommand(IntPtr menu, int command, string text, uint extraFlags = 0)
    {
        if (!NativeMethods.AppendMenu(menu, NativeMethods.MfString | extraFlags, new UIntPtr((uint)command), text))
            Log.Warning("AppendMenu failed for command {Command}. LastWin32Error={LastWin32Error}",
                command,
                Marshal.GetLastWin32Error());
    }

    private static void AppendPopup(IntPtr menu, IntPtr submenu, string text, bool isChecked)
    {
        var flags = NativeMethods.MfPopup | (isChecked ? NativeMethods.MfChecked : 0);
        if (!NativeMethods.AppendMenu(menu, flags, (UIntPtr)submenu, text))
            Log.Warning("AppendMenu failed for popup {Text}. LastWin32Error={LastWin32Error}",
                text,
                Marshal.GetLastWin32Error());
    }

    private static void AppendSeparator(IntPtr menu)
    {
        if (!NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, UIntPtr.Zero, null))
            Log.Warning("AppendMenu failed for separator. LastWin32Error={LastWin32Error}",
                Marshal.GetLastWin32Error());
    }

    private void DispatchMenuCommand(int command)
    {
        switch (command)
        {
            case 0:
                return;
            case CommandSettings:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                return;
            case CommandRefresh:
                RefreshRequested?.Invoke(this, EventArgs.Empty);
                return;
            case CommandExit:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                return;
            case CommandEyeToggle:
                EyeProtectionToggleRequested?.Invoke(this, EventArgs.Empty);
                return;
            case CommandBoostToggle:
                BrightnessBoostToggleRequested?.Invoke(this, EventArgs.Empty);
                return;
        }

        if (command >= CommandEyePresetBase && command < CommandEyePresetBase + PresetHours.Length)
        {
            EyeProtectionPresetRequested?.Invoke(this, PresetHours[command - CommandEyePresetBase]);
            return;
        }

        if (command >= CommandBoostPresetBase && command < CommandBoostPresetBase + PresetHours.Length)
            BrightnessBoostPresetRequested?.Invoke(this, PresetHours[command - CommandBoostPresetBase]);
    }

    public void Dispose()
    {
        if (_registered)
        {
            var data = CreateNotifyIconData(string.Empty);
            if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref data))
                Log.Warning("Shell_NotifyIcon(NIM_DELETE) failed. LastWin32Error={LastWin32Error}",
                    Marshal.GetLastWin32Error());
            _registered = false;
        }

        if (_iconHandle != IntPtr.Zero && _ownsIconHandle)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
        }

        NativeMethods.UnregisterClass(_windowClassName, _moduleHandle);
    }

    private static void ThrowLastWin32Error(string message)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), message);
    }

    private static partial class NativeMethods
    {
        public const int WmApp = 0x8000;
        public const int WmLButtonUp = 0x0202;
        public const int WmRButtonUp = 0x0205;
        public const int WmContextMenu = 0x007B;
        public const int NinSelect = 0x0400;
        public const int NinKeySelect = 0x0401;

        public const uint NifMessage = 0x00000001;
        public const uint NifIcon = 0x00000002;
        public const uint NifTip = 0x00000004;
        public const uint NimAdd = 0x00000000;
        public const uint NimModify = 0x00000001;
        public const uint NimDelete = 0x00000002;
        public const uint NimSetVersion = 0x00000004;
        public const uint NotifyIconVersion4 = 4;

        public const uint MfString = 0x00000000;
        public const uint MfPopup = 0x00000010;
        public const uint MfSeparator = 0x00000800;
        public const uint MfChecked = 0x00000008;

        public const uint TpmRightButton = 0x00000002;
        public const uint TpmReturNcmd = 0x00000100;

        public const int IdiApplication = 32512;

        public delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WndClassEx
        {
            public int cbSize;
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NotifyIconData
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public uint uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128, ArraySubType = UnmanagedType.U2)]
            public char[] szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256, ArraySubType = UnmanagedType.U2)]
            public char[] szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64, ArraySubType = UnmanagedType.U2)]
            public char[] szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

        [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int ExtractIconEx(
            string lpszFile,
            int nIconIndex,
            [Out] IntPtr[] phiconLarge,
            [Out] IntPtr[] phiconSmall,
            int nIcons);

        [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
        public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out Point lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int TrackPopupMenuEx(
            IntPtr hMenu,
            uint uFlags,
            int x,
            int y,
            IntPtr hWnd,
            IntPtr lptpm);
    }
}
