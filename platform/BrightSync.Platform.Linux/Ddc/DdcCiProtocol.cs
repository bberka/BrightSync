using System.Text;

namespace BrightSync.Platform.Linux.Ddc;

/// <summary>
/// VESA DDC/CI framing over an <see cref="II2cBus"/>: Get/Set VCP Feature and Capabilities Request.
/// Frames are <c>[source 0x51][0x80|length][payload...][checksum]</c> on write and
/// <c>[0x6E][0x80|length][payload...][checksum]</c> on read.
/// </summary>
internal sealed class DdcCiProtocol(II2cBus bus, Action<int>? delay = null)
{
    private const byte HostAddress = 0x51;
    private const byte DisplayWriteAddress = 0x6E;
    private const byte VirtualHostAddress = 0x50;
    private const int CommandSpacingMilliseconds = 50;
    private const int ReplyDelayMilliseconds = 50;
    private const int MaxCapabilitiesBytes = 4096;

    private readonly Action<int> _delay = delay ?? (static ms => Thread.Sleep(ms));

    public bool TrySetVcp(byte code, uint value)
    {
        var frame = BuildFrame([0x03, code, (byte)(value >> 8), (byte)value]);
        var ok = bus.Write(frame);
        _delay(CommandSpacingMilliseconds);
        return ok;
    }

    public bool TryGetVcp(byte code, out uint current, out uint maximum)
    {
        current = 0;
        maximum = 0;

        if (!bus.Write(BuildFrame([0x01, code])))
            return false;

        _delay(ReplyDelayMilliseconds);

        Span<byte> reply = stackalloc byte[11];
        if (bus.Read(reply) < reply.Length)
            return false;

        if (!ValidateReply(reply, out var length) || length != 8)
            return false;

        // payload: 02 result code type maxHi maxLo curHi curLo
        if (reply[2] != 0x02 || reply[3] != 0x00 || reply[4] != code)
            return false;

        maximum = (uint)((reply[6] << 8) | reply[7]);
        current = (uint)((reply[8] << 8) | reply[9]);
        return true;
    }

    /// <summary>Reads the full MCCS capabilities string, or null when the display does not answer.</summary>
    public string? ReadCapabilities()
    {
        var text = new StringBuilder();
        var offset = 0;
        Span<byte> reply = stackalloc byte[38];

        while (offset < MaxCapabilitiesBytes)
        {
            if (!bus.Write(BuildFrame([0xF3, (byte)(offset >> 8), (byte)offset])))
                return null;

            _delay(ReplyDelayMilliseconds);

            reply.Clear();
            if (bus.Read(reply) < 3 || !ValidateReply(reply, out var length) || length < 3)
                return null;

            if (reply[2] != 0xE3)
                return null;

            var fragmentLength = length - 3;
            if (fragmentLength == 0)
                break;

            text.Append(Encoding.ASCII.GetString(reply.Slice(5, fragmentLength)));
            offset += fragmentLength;
            _delay(CommandSpacingMilliseconds);
        }

        var result = text.ToString().Trim('\0', ' ');
        return result.Length == 0 ? null : result;
    }

    internal static byte[] BuildFrame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 3];
        frame[0] = HostAddress;
        frame[1] = (byte)(0x80 | payload.Length);
        payload.CopyTo(frame.AsSpan(2));

        var checksum = (byte)DisplayWriteAddress;
        for (var i = 0; i < frame.Length - 1; i++)
            checksum ^= frame[i];

        frame[^1] = checksum;
        return frame;
    }

    /// <summary>Checks framing and checksum. <paramref name="length"/> is the payload length from the header.</summary>
    internal static bool ValidateReply(ReadOnlySpan<byte> reply, out int length)
    {
        length = 0;
        if (reply.Length < 3 || reply[0] != DisplayWriteAddress || (reply[1] & 0x80) == 0)
            return false;

        length = reply[1] & 0x7F;
        if (length == 0 || reply.Length < length + 3)
            return false;

        var checksum = VirtualHostAddress;
        for (var i = 0; i < length + 2; i++)
            checksum ^= reply[i];

        return checksum == reply[length + 2];
    }
}
