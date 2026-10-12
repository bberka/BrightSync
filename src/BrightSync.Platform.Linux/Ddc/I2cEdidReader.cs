namespace BrightSync.Platform.Linux.Ddc;

/// <summary>Reads the 128-byte EDID base block from a bus opened at slave address 0x50.</summary>
internal static class I2cEdidReader
{
    public const int BlockSize = 128;

    public static byte[]? Read(II2cBus bus)
    {
        var block = new byte[BlockSize];
        if (!bus.Write([0x00]))
            return null;

        if (bus.Read(block) == BlockSize)
            return block;

        // Some adapters refuse long reads: reset the EEPROM pointer and read byte by byte.
        if (!bus.Write([0x00]))
            return null;

        Span<byte> one = stackalloc byte[1];
        for (var i = 0; i < BlockSize; i++)
        {
            if (bus.Read(one) != 1)
                return null;

            block[i] = one[0];
        }

        return block;
    }
}
