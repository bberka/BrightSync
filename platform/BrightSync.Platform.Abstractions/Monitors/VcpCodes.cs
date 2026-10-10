namespace BrightSync.Core.Monitors;

/// <summary>VESA MCCS VCP feature codes BrightSync reads or writes.</summary>
public static class VcpCodes
{
    public const byte Brightness = 0x10;
    public const byte Contrast = 0x12;
    public const byte ColorPreset = 0x14;
    public const byte RedGain = 0x16;
    public const byte GreenGain = 0x18;
    public const byte BlueGain = 0x1A;
    public const byte InputSource = 0x60;
    public const byte Volume = 0x62;
    public const byte Gamma = 0x72;
    public const byte Sharpness = 0x87;
    public const byte Saturation = 0x8A;
    public const byte PowerControl = 0xD6;
}
