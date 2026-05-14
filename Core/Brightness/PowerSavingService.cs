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

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data; // First byte of data
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, uint Flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

    private sealed class HiddenWindow : System.Windows.Forms.NativeWindow, IDisposable
    {
        public event EventHandler<WindowMessageEventArgs>? MessageReceived;

        public HiddenWindow()
        {
            CreateHandle(new System.Windows.Forms.CreateParams());
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            base.WndProc(ref m);
            MessageReceived?.Invoke(this, new WindowMessageEventArgs(m.Msg, m.WParam, m.LParam));
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }

    private sealed class WindowMessageEventArgs(int msg, IntPtr wParam, IntPtr lParam) : EventArgs
    {
        public int Msg { get; } = msg;
        public IntPtr WParam { get; } = wParam;
        public IntPtr LParam { get; } = lParam;
    }
}
