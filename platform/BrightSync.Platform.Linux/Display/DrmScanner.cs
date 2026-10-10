using System.Text.RegularExpressions;

namespace BrightSync.Platform.Linux.Display;

/// <summary>Read-only view of sysfs so hardware discovery can be tested against fixture trees.</summary>
internal interface ISysfs
{
    string? ReadText(string path);
    byte[]? ReadBytes(string path);
    IReadOnlyList<string> ListDirectories(string path);

    /// <summary>Final path segment of the symlink target, or null when <paramref name="path"/> is not a link.</summary>
    string? ReadLinkName(string path);
}

internal sealed class RealSysfs : ISysfs
{
    public string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public byte[]? ReadBytes(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> ListDirectories(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.GetDirectories(path).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string? ReadLinkName(string path)
    {
        try
        {
            var target = new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;
            return target is null ? null : Path.GetFileName(target.TrimEnd('/'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>One physical display connector reported by the kernel DRM subsystem.</summary>
internal sealed record DrmConnector(
    string CardName,
    string Name,
    string ConnectionType,
    bool IsInternal,
    Edid? Edid,
    byte[]? EdidBytes,
    int? I2cBus,
    int Width,
    int Height)
{
    public string SysfsName => $"{CardName}-{Name}";
}

internal static partial class DrmScanner
{
    private const string DrmRoot = "/sys/class/drm";

    public static IReadOnlyList<DrmConnector> Scan(ISysfs sysfs, string root = DrmRoot)
    {
        var connectors = new List<DrmConnector>();
        foreach (var entry in sysfs.ListDirectories(root))
        {
            var match = ConnectorRegex().Match(entry);
            if (!match.Success)
                continue;

            var path = $"{root}/{entry}";
            if (!string.Equals(sysfs.ReadText($"{path}/status"), "connected", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = match.Groups["name"].Value;
            var edidBytes = sysfs.ReadBytes($"{path}/edid");
            if (edidBytes is { Length: 0 })
                edidBytes = null;

            ParseFirstMode(sysfs.ReadText($"{path}/modes"), out var width, out var height);
            connectors.Add(new DrmConnector(
                match.Groups["card"].Value,
                name,
                MapConnectionType(name),
                IsInternalConnector(name),
                edidBytes is null ? null : Edid.Parse(edidBytes),
                edidBytes,
                FindI2cBus(sysfs, path),
                width,
                height));
        }

        return connectors;
    }

    internal static string MapConnectionType(string connectorName)
    {
        var type = TypeRegex().Match(connectorName).Groups["type"].Value;
        return type.ToUpperInvariant() switch
        {
            "HDMI-A" or "HDMI-B" => "HDMI",
            "DP" => "DP",
            "EDP" => "eDP",
            "DVI-D" or "DVI-I" or "DVI-A" or "DVI" => "DVI",
            "VGA" => "VGA",
            "LVDS" => "LVDS",
            "DSI" => "DSI",
            "VIRTUAL" => "Virtual",
            "" => string.Empty,
            _ => type
        };
    }

    internal static bool IsInternalConnector(string connectorName)
        => MapConnectionType(connectorName) is "eDP" or "LVDS" or "DSI";

    internal static void ParseFirstMode(string? modes, out int width, out int height)
    {
        width = 0;
        height = 0;
        var first = modes?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null)
            return;

        var match = ModeRegex().Match(first);
        if (!match.Success)
            return;

        width = int.Parse(match.Groups["w"].Value);
        height = int.Parse(match.Groups["h"].Value);
    }

    private static int? FindI2cBus(ISysfs sysfs, string connectorPath)
    {
        var ddc = sysfs.ReadLinkName($"{connectorPath}/ddc");
        if (ddc is not null && TryParseBus(ddc, out var bus))
            return bus;

        foreach (var child in sysfs.ListDirectories(connectorPath))
        {
            if (TryParseBus(child, out bus))
                return bus;
        }

        return null;
    }

    private static bool TryParseBus(string name, out int bus)
    {
        bus = 0;
        return name.StartsWith("i2c-", StringComparison.Ordinal) &&
               int.TryParse(name.AsSpan(4), out bus);
    }

    [GeneratedRegex(@"^(?<card>card\d+)-(?<name>.+)$")]
    private static partial Regex ConnectorRegex();

    [GeneratedRegex(@"^(?<type>.+?)-\d+$")]
    private static partial Regex TypeRegex();

    [GeneratedRegex(@"^(?<w>\d+)x(?<h>\d+)")]
    private static partial Regex ModeRegex();
}
