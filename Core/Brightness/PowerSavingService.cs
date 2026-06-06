using System.Runtime.InteropServices;
using BrightSync.Core.Config;
using Microsoft.Win32;
using Serilog;

namespace BrightSync.Core.Brightness;

public sealed class PowerSavingService : IDisposable
{
    public event EventHandler<bool>? EnergySaverStatusChanged;

    private readonly BrightSyncEngine _engine;
    private readonly ConfigManager _config;
    private bool _isEnergySaverActive;
    private bool _disposed;
    private IntPtr _hPowerNotify = IntPtr.Zero;
    private HiddenWindow? _hiddenWindow;

    public bool IsEnergySaverActive => _isEnergySaverActive;

    public PowerSavingService(BrightSyncEngine engine, ConfigManager config)
    {
        _engine = engine;
        _config = config;
    }

    public void Start()
    {
        _hiddenWindow = new HiddenWindow();
        _hiddenWindow.MessageReceived += OnWindowMessage;

        var guid = new Guid("E61035A1-AD3D-4787-8336-B99F7D0E33EF"); // GUID_POWER_SAVING_STATUS
        _hPowerNotify = RegisterPowerSettingNotification(_hiddenWindow.Handle, ref guid, 0);

        if (_hPowerNotify == IntPtr.Zero)
        {
            Log.Warning("Failed to register for energy saver notifications (Error={Error})", Marshal.GetLastWin32Error());
        }

        Log.Information("Power saving service started. EnergySaverReductionEnabled={Enabled}", _config.Config.EnergySaverReductionEnabled);
    }

    private void OnWindowMessage(object? sender, WindowMessageEventArgs e)
    {
        if (e.Msg == WM_POWERBROADCAST && e.WParam == (IntPtr)PBT_POWERSETTINGCHANGE)
        {
            var settings = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(e.LParam);
            var guid = new Guid("E61035A1-AD3D-4787-8336-B99F7D0E33EF");

            if (settings.PowerSetting == guid)
            {
                var isActive = settings.Data != 0;
                if (_isEnergySaverActive != isActive)
                {
                    _isEnergySaverActive = isActive;
                    Log.Information("Windows Energy Saver is now {Status}", isActive ? "Active" : "Inactive");
                    
                    if (_config.Config.EnergySaverReductionEnabled)
                    {
                        _engine.ForceSync();
                    }
                    
                    EnergySaverStatusChanged?.Invoke(this, isActive);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hPowerNotify != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_hPowerNotify);
            _hPowerNotify = IntPtr.Zero;
        }

        _hiddenWindow?.Dispose();
    }

    // --- Native ---

    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int WM_DESTROY = 0x0002;
    private const int WM_CLOSE = 0x0010;
    private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data; // First byte of data
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, uint Flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam
    );

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    private sealed class HiddenWindow : IDisposable
    {
        public event EventHandler<WindowMessageEventArgs>? MessageReceived;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private readonly WndProcDelegate _wndProc;
        private readonly Thread _thread;
        private readonly ManualResetEvent _initEvent = new(false);
        private bool _disposed;

        private IntPtr _hWnd = IntPtr.Zero;
        private string? _className;
        private ushort _classAtom;

        public IntPtr Handle => _hWnd;

        public HiddenWindow()
        {
            _wndProc = WndProc;
            _thread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "PowerSavingHiddenWindow"
            };
            _thread.Start();
            _initEvent.WaitOne();
        }

        private void RunMessageLoop()
        {
            string className = "BrightSyncPowerSavingWindowClass_" + Guid.NewGuid();
            WNDCLASSEX wndClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                lpszClassName = className
            };

            ushort atom = RegisterClassEx(ref wndClass);
            if (atom == 0)
            {
                _initEvent.Set();
                return;
            }

            _classAtom = atom;
            _className = className;

            IntPtr hWnd = CreateWindowEx(
                0,
                className,
                "BrightSyncPowerSavingWindow",
                0,
                0, 0, 0, 0,
                HWND_MESSAGE,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero
            );

            _hWnd = hWnd;
            _initEvent.Set();

            if (hWnd != IntPtr.Zero)
            {
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            MessageReceived?.Invoke(this, new WindowMessageEventArgs((int)msg, wParam, lParam));
            if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hWnd != IntPtr.Zero)
            {
                PostMessage(_hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            _thread.Join(2000);

            if (_className != null)
            {
                UnregisterClass(_className, IntPtr.Zero);
            }
            _initEvent.Dispose();
        }
    }

    private sealed class WindowMessageEventArgs(int msg, IntPtr wParam, IntPtr lParam) : EventArgs
    {
        public int Msg { get; } = msg;
        public IntPtr WParam { get; } = wParam;
        public IntPtr LParam { get; } = lParam;
    }
}
