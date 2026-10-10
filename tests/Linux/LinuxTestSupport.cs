using System.Text;
using BrightSync.Platform.Linux.Ddc;
using BrightSync.Platform.Linux.Display;

namespace BrightSync.Tests.Linux;

/// <summary>In-memory sysfs tree. Paths use forward slashes like the real filesystem.</summary>
internal sealed class FakeSysfs : ISysfs
{
    private readonly Dictionary<string, byte[]> _files = new();
    private readonly Dictionary<string, string> _links = new();
    private readonly HashSet<string> _directories = new();

    public FakeSysfs AddFile(string path, string text) => AddFile(path, Encoding.ASCII.GetBytes(text));

    public FakeSysfs AddFile(string path, byte[] data)
    {
        _files[path] = data;
        AddParents(path);
        return this;
    }

    public FakeSysfs AddLink(string path, string targetName)
    {
        _links[path] = targetName;
        AddParents(path);
        return this;
    }

    public FakeSysfs AddDirectory(string path)
    {
        _directories.Add(path);
        AddParents(path);
        return this;
    }

    public string? ReadText(string path)
        => _files.TryGetValue(path, out var data) ? Encoding.ASCII.GetString(data).Trim() : null;

    public byte[]? ReadBytes(string path) => _files.TryGetValue(path, out var data) ? data : null;

    public IReadOnlyList<string> ListDirectories(string path)
        => _directories
            .Where(d => d.StartsWith(path + "/", StringComparison.Ordinal) && !d[(path.Length + 1)..].Contains('/'))
            .Select(d => d[(path.Length + 1)..])
            .Order(StringComparer.Ordinal)
            .ToArray();

    public string? ReadLinkName(string path) => _links.GetValueOrDefault(path);

    private void AddParents(string path)
    {
        var index = path.LastIndexOf('/');
        while (index > 0)
        {
            path = path[..index];
            _directories.Add(path);
            index = path.LastIndexOf('/');
        }
    }
}

internal static class EdidFixture
{
    /// <summary>Builds a valid 128-byte EDID base block for the given identity.</summary>
    public static byte[] Build(string manufacturer, ushort product, uint serial, string name, string serialText = "")
    {
        var block = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(block, 0);

        var vendor = ((manufacturer[0] - 'A' + 1) << 10) | ((manufacturer[1] - 'A' + 1) << 5) | (manufacturer[2] - 'A' + 1);
        block[8] = (byte)(vendor >> 8);
        block[9] = (byte)vendor;
        block[10] = (byte)product;
        block[11] = (byte)(product >> 8);
        block[12] = (byte)serial;
        block[13] = (byte)(serial >> 8);
        block[14] = (byte)(serial >> 16);
        block[15] = (byte)(serial >> 24);
        block[21] = 60;
        block[22] = 34;

        WriteDescriptor(block, 54, 0xFC, name);
        if (serialText.Length > 0)
            WriteDescriptor(block, 72, 0xFF, serialText);

        var sum = 0;
        for (var i = 0; i < 127; i++)
            sum += block[i];
        block[127] = (byte)((256 - sum % 256) % 256);
        return block;
    }

    private static void WriteDescriptor(byte[] block, int offset, byte tag, string text)
    {
        block[offset + 3] = tag;
        var bytes = Encoding.ASCII.GetBytes(text + "\n");
        for (var i = 0; i < 13; i++)
            block[offset + 5 + i] = i < bytes.Length ? bytes[i] : (byte)0x20;
    }
}

/// <summary>Scripted I2C display that answers DDC/CI frames like a real monitor.</summary>
internal sealed class FakeDisplayBus : II2cBus
{
    private readonly Dictionary<byte, (uint Current, uint Max)> _features = new();
    private readonly string _capabilities;
    private byte[] _pendingReply = [];

    public FakeDisplayBus(string capabilities = "(prot(monitor)type(lcd)vcp(10 12))")
    {
        _capabilities = capabilities;
    }

    public List<(byte Code, uint Value)> Writes { get; } = [];
    public bool Disposed { get; private set; }
    public bool FailWrites { get; set; }
    public bool Silent { get; set; }

    public FakeDisplayBus WithFeature(byte code, uint current, uint max)
    {
        _features[code] = (current, max);
        return this;
    }

    public bool Write(ReadOnlySpan<byte> data)
    {
        if (FailWrites)
            return false;

        // frame: 51 8N payload... checksum
        var payload = data.Slice(2, data[1] & 0x7F);
        _pendingReply = [];
        switch (payload[0])
        {
            case 0x03:
                var code = payload[1];
                var value = (uint)((payload[2] << 8) | payload[3]);
                Writes.Add((code, value));
                _features[code] = (value, _features.TryGetValue(code, out var f) ? f.Max : 100);
                break;
            case 0x01 when !Silent:
                _pendingReply = _features.TryGetValue(payload[1], out var feature)
                    ? Reply([0x02, 0x00, payload[1], 0x00, (byte)(feature.Max >> 8), (byte)feature.Max,
                        (byte)(feature.Current >> 8), (byte)feature.Current])
                    : Reply([0x02, 0x01, payload[1], 0x00, 0, 0, 0, 0]);
                break;
            case 0xF3 when !Silent:
                var offset = (payload[1] << 8) | payload[2];
                var chunk = Encoding.ASCII.GetBytes(_capabilities.Length > offset
                    ? _capabilities.Substring(offset, Math.Min(32, _capabilities.Length - offset))
                    : string.Empty);
                _pendingReply = Reply([0xE3, payload[1], payload[2], .. chunk]);
                break;
        }

        return true;
    }

    public int Read(Span<byte> buffer)
    {
        if (_pendingReply.Length == 0)
            return -1;

        _pendingReply.AsSpan(0, Math.Min(buffer.Length, _pendingReply.Length)).CopyTo(buffer);
        return Math.Min(buffer.Length, _pendingReply.Length);
    }

    public void Dispose() => Disposed = true;

    private static byte[] Reply(byte[] payload)
    {
        var frame = new byte[payload.Length + 3];
        frame[0] = 0x6E;
        frame[1] = (byte)(0x80 | payload.Length);
        payload.CopyTo(frame, 2);
        byte checksum = 0x50;
        for (var i = 0; i < frame.Length - 1; i++)
            checksum ^= frame[i];
        frame[^1] = checksum;
        return frame;
    }
}
