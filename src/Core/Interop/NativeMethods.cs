using System.Runtime.InteropServices;

namespace BrightSync.Core.Interop;

internal static class NativeMethods
{
    public const byte VCP_BRIGHTNESS = 0x10;
    public const byte VCP_CONTRAST = 0x12;
    public const byte VCP_COLOR_PRESET = 0x14;
    public const byte VCP_RED_GAIN = 0x16;
    public const byte VCP_GREEN_GAIN = 0x18;
    public const byte VCP_BLUE_GAIN = 0x1A;
    public const byte VCP_VOLUME = 0x62;
    public const byte VCP_INPUT_SOURCE = 0x60;
    public const byte VCP_SHARPNESS = 0x87;
    public const byte VCP_SATURATION = 0x8A;
    public const byte VCP_GAMMA = 0x72;
    public const byte VCP_POWER_CONTROL = 0xD6;
    public const int ENUM_CURRENT_SETTINGS = -1;
    public const uint DM_DISPLAYFREQUENCY = 0x00400000;
    public const uint CDS_UPDATEREGISTRY = 0x00000001;
    public const int DISP_CHANGE_SUCCESSFUL = 0;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 15;
    public const uint MC_CAPS_BRIGHTNESS = 0x00000002;

    // --- DDC/CI (dxva2.dll) ---

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        uint dwPhysicalMonitorArraySize,
        [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool DestroyPhysicalMonitors(
        uint dwPhysicalMonitorArraySize,
        [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetVCPFeatureAndVCPFeatureReply(
        IntPtr hMonitor,
        byte bVCPCode,
        out MC_VCP_CODE_TYPE pvct,
        out uint pdwCurrentValue,
        out uint pdwMaximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetVCPFeature(
        IntPtr hMonitor,
        byte bVCPCode,
        uint dwNewValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetMonitorCapabilities(
        IntPtr hMonitor,
        out uint pdwMonitorCapabilities,
        out uint pdwSupportedColorTemperatures);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetMonitorBrightness(
        IntPtr hMonitor,
        out uint pdwMinimumBrightness,
        out uint pdwCurrentBrightness,
        out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool SetMonitorBrightness(
        IntPtr hMonitor,
        uint dwNewBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    public static extern bool GetCapabilitiesStringLength(
        IntPtr hMonitor,
        out uint pdwCapabilitiesStringLengthInCharacters);

    [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern bool CapabilitiesRequestAndCapabilitiesReply(
        IntPtr hMonitor,
        [Out] byte[] pszASCIICapabilitiesString,
        uint dwCapabilitiesStringLengthInCharacters);

    // --- Display enumeration (user32.dll) ---

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(
        string? lpDevice,
        uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice,
        uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(
        IntPtr hMonitor,
        ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(
        string lpszDeviceName,
        int iModeNum,
        ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(
        string? lpszDeviceName,
        ref DEVMODE lpDevMode,
        IntPtr hwnd,
        uint dwflags,
        IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SDR_WHITE_LEVEL requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 requestPacket);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINTL lpPoint);

    public delegate bool MonitorEnumProc(
        IntPtr hMonitor,
        IntPtr hdcMonitor,
        ref RECT lprcMonitor,
        IntPtr dwData);

    // --- Color Management (mscms.dll) ---

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetColorProfileDirectory(
        string? pMachineName,
        System.Text.StringBuilder pBuffer,
        ref uint pdwSize);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AssociateColorProfileWithDevice(
        string? pMachineName,
        string pProfileName,
        string pDeviceName);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool DisassociateColorProfileFromDevice(
        string? pMachineName,
        string pProfileName,
        string pDeviceName);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WcsSetDefaultColorProfile(
        int scope,
        string pDeviceName,
        int cptColorProfileType,
        int cpstColorProfileSubType,
        uint dwProfileID,
        string pProfileName);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WcsGetDefaultColorProfile(
        int scope,
        string pDeviceName,
        int cptColorProfileType,
        int cpstColorProfileSubType,
        uint dwProfileID,
        uint cbProfileName,
        [Out] byte[] pProfileName);

    // --- Structs ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    public enum MC_VCP_CODE_TYPE
    {
        MC_MOMENTARY,
        MC_SET_PARAMETER
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public uint cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY outputTechnology;
        public DISPLAYCONFIG_ROTATION rotation;
        public DISPLAYCONFIG_SCALING scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public DISPLAYCONFIG_SCANLINE_ORDERING scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    public enum DISPLAYCONFIG_MODE_INFO_TYPE : uint
    {
        Source = 1,
        Target = 2,
        DesktopImage = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        public DISPLAYCONFIG_MODE_INFO_TYPE infoType;
        public uint id;
        public LUID adapterId;
        public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct DISPLAYCONFIG_MODE_INFO_UNION
    {
        [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE targetMode;
        [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint width;
        public uint height;
        public PIXELFORMAT pixelFormat;
        public POINTL position;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_MODE
    {
        public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq;
        public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public DISPLAYCONFIG_2DREGION activeSize;
        public DISPLAYCONFIG_2DREGION totalSize;
        public uint videoStandard;
        public DISPLAYCONFIG_SCANLINE_ORDERING scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_2DREGION
    {
        public uint cx;
        public uint cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL
    {
        public int x;
        public int y;
    }

    public enum PIXELFORMAT : uint
    {
        Pixelformat8Bpp = 1,
        Pixelformat16Bpp = 2,
        Pixelformat24Bpp = 3,
        Pixelformat32Bpp = 4,
        PixelformatNongdi = 5
    }

    public enum DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY : uint
    {
        Other = 0xFFFFFFFF,
        Hd15 = 0,
        Svideo = 1,
        CompositeVideo = 2,
        ComponentVideo = 3,
        Dvi = 4,
        Hdmi = 5,
        Lvds = 6,
        Djpn = 8,
        Sdi = 9,
        DisplayPortExternal = 10,
        DisplayPortEmbedded = 11,
        UdiExternal = 12,
        UdiEmbedded = 13,
        Sdtvdongle = 14,
        Miracast = 15,
        Internal = 0x80000000,
        ForceUint32 = 0xFFFFFFFF
    }

    public enum DISPLAYCONFIG_ROTATION : uint
    {
        Identity = 1,
        Rotate90 = 2,
        Rotate180 = 3,
        Rotate270 = 4,
        ForceUint32 = 0xFFFFFFFF
    }

    public enum DISPLAYCONFIG_SCALING : uint
    {
        Identity = 1,
        Centered = 2,
        Stretched = 3,
        AspectRatioCenteredMax = 4,
        Custom = 5,
        Preferred = 128,
        ForceUint32 = 0xFFFFFFFF
    }

    public enum DISPLAYCONFIG_SCANLINE_ORDERING : uint
    {
        Unspecified = 0,
        Progressive = 1,
        Interlaced = 2,
        InterlacedUpperFieldeFirst = Interlaced,
        InterlacedLowerFieldFirst = 3,
        ForceUint32 = 0xFFFFFFFF
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    public enum DISPLAYCONFIG_DEVICE_INFO_TYPE : uint
    {
        GetSourceName = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
        GetTargetName = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
        GetAdvancedColorInfo = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
        GetSdrWhiteLevel = DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
        GetAdvancedColorInfo2 = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2
    }

    public enum DISPLAYCONFIG_COLOR_ENCODING : uint
    {
        Rgb = 0,
        Ycbcr444 = 1,
        Ycbcr422 = 2,
        Ycbcr420 = 3,
        Intensity = 4,
        ForceUint32 = 0xFFFFFFFF
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS flags;
        public DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public DISPLAYCONFIG_COLOR_ENCODING colorEncoding;
        public uint bitsPerColorChannel;
    }

    public enum DISPLAYCONFIG_ADVANCED_COLOR_MODE : uint
    {
        Sdr = 0,
        Wcg = 1,
        Hdr = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
        public DISPLAYCONFIG_COLOR_ENCODING colorEncoding;
        public uint bitsPerColorChannel;
        public DISPLAYCONFIG_ADVANCED_COLOR_MODE activeColorMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SDR_WHITE_LEVEL
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint SDRWhiteLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS
    {
        public uint value;
    }
}