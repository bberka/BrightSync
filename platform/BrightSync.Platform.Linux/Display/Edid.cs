using System.Text;

namespace BrightSync.Platform.Linux.Display;

/// <summary>Fields BrightSync needs from a 128-byte EDID base block.</summary>
internal sealed record Edid(
    string ManufacturerCode,
    ushort ProductCode,
    uint SerialNumber,
    string ModelName,
    string SerialString,
    int WidthCm,
    int HeightCm,
    bool ChecksumValid)
{
    private const int BlockSize = 128;
    private static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    /// <summary>PnP-style hardware ID such as <c>DEL4141</c>.</summary>
    public string HardwareId => $"{ManufacturerCode}{ProductCode:X4}";

    /// <summary>Identity text stable across reboots and ports: vendor, product, and serial.</summary>
    public string StableKey
    {
        get
        {
            var serial = !string.IsNullOrWhiteSpace(SerialString)
                ? SerialString
                : SerialNumber != 0
                    ? SerialNumber.ToString("X8")
                    : string.Empty;
            return string.IsNullOrEmpty(serial) ? HardwareId : $"{HardwareId}\\{serial}";
        }
    }

    public static Edid? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < BlockSize || !data[..Header.Length].SequenceEqual(Header))
            return null;

        var vendor = (data[8] << 8) | data[9];
        var manufacturer = string.Create(3, vendor, static (span, v) =>
        {
            span[0] = DecodeLetter((v >> 10) & 0x1F);
            span[1] = DecodeLetter((v >> 5) & 0x1F);
            span[2] = DecodeLetter(v & 0x1F);
        });

        var product = (ushort)(data[10] | (data[11] << 8));
        var serialNumber = (uint)(data[12] | (data[13] << 8) | (data[14] << 16) | (data[15] << 24));

        var name = string.Empty;
        var serialText = string.Empty;
        for (var offset = 54; offset <= 108; offset += 18)
        {
            var descriptor = data.Slice(offset, 18);
            if (descriptor[0] != 0 || descriptor[1] != 0 || descriptor[2] != 0)
                continue;

            switch (descriptor[3])
            {
                case 0xFC:
                    name = DecodeText(descriptor[5..]);
                    break;
                case 0xFF:
                    serialText = DecodeText(descriptor[5..]);
                    break;
            }
        }

        var sum = 0;
        for (var i = 0; i < BlockSize; i++)
            sum += data[i];

        return new Edid(manufacturer, product, serialNumber, name, serialText, data[21], data[22], sum % 256 == 0);
    }

    private static char DecodeLetter(int value)
        => value is >= 1 and <= 26 ? (char)('A' + value - 1) : '?';

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0x0A);
        if (end >= 0)
            bytes = bytes[..end];

        return Encoding.ASCII.GetString(bytes).Trim().TrimEnd('\0');
    }
}
