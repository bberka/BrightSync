using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BrightSync.Platform.Linux.Ddc;

/// <summary>Raw byte transport to one DDC/CI endpoint (slave address 0x37) on an I2C adapter.</summary>
internal interface II2cBus : IDisposable
{
    /// <summary>Writes the whole buffer. Returns false on any short or failed write.</summary>
    bool Write(ReadOnlySpan<byte> data);

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes. Returns the count read, or -1 on failure.</summary>
    int Read(Span<byte> buffer);
}

/// <summary>I2C adapter opened through <c>/dev/i2c-N</c> with the <c>i2c-dev</c> kernel module.</summary>
internal sealed unsafe partial class LinuxI2cBus : II2cBus
{
    private const nuint I2cSlaveForce = 0x0706;
    internal const byte DdcAddress = 0x37;
    internal const byte EdidAddress = 0x50;

    private readonly SafeFileHandle _handle;

    private LinuxI2cBus(SafeFileHandle handle) => _handle = handle;

    /// <summary>Opens <c>/dev/i2c-<paramref name="bus"/></c> and selects <paramref name="address"/>. Null when inaccessible.</summary>
    public static LinuxI2cBus? TryOpen(int bus, byte address = DdcAddress)
    {
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle($"/dev/i2c-{bus}", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (Ioctl((int)handle.DangerousGetHandle(), I2cSlaveForce, address) < 0)
        {
            handle.Dispose();
            return null;
        }

        return new LinuxI2cBus(handle);
    }

    public bool Write(ReadOnlySpan<byte> data)
    {
        fixed (byte* pointer = data)
        {
            var written = WriteNative((int)_handle.DangerousGetHandle(), pointer, (nuint)data.Length);
            return written == data.Length;
        }
    }

    public int Read(Span<byte> buffer)
    {
        fixed (byte* pointer = buffer)
        {
            return (int)ReadNative((int)_handle.DangerousGetHandle(), pointer, (nuint)buffer.Length);
        }
    }

    public void Dispose() => _handle.Dispose();

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int fd, nuint request, nuint argument);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static partial nint WriteNative(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint ReadNative(int fd, byte* buffer, nuint count);
}
