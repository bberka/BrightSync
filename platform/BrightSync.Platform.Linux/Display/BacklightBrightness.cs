using System.Globalization;
using Serilog;

namespace BrightSync.Platform.Linux.Display;

/// <summary>
/// Built-in panel brightness through <c>/sys/class/backlight</c>. Writes go straight to sysfs when the
/// user has permission (udev rule / <c>video</c> group) and otherwise through logind's
/// <c>SetBrightness</c>, which needs no privileges for the active session.
/// </summary>
internal sealed class BacklightBrightness(
    ISysfs sysfs,
    Func<string, uint, bool> writeThroughLogind,
    Func<string, string, bool>? writeSysfs = null) : IInternalBrightness
{
    private const string BacklightRoot = "/sys/class/backlight";

    private readonly Func<string, string, bool> _writeSysfs = writeSysfs ?? TryWriteSysfs;

    public bool IsAvailable => FindDevice() is not null;

    public int ReadCurrentBrightness()
    {
        var device = FindDevice();
        if (device is null)
            return -1;

        var current = ReadInt($"{device.Path}/actual_brightness") ?? ReadInt($"{device.Path}/brightness");
        return current is null ? -1 : ToPercent(current.Value, device.Max);
    }

    public bool TrySetBrightness(int brightnessPercent)
    {
        var device = FindDevice();
        if (device is null)
            return false;

        var raw = ToRaw(Math.Clamp(brightnessPercent, 0, 100), device.Max);
        if (_writeSysfs($"{device.Path}/brightness", raw.ToString(CultureInfo.InvariantCulture)))
            return true;

        Log.Debug("Direct backlight write to {Device} was denied; trying logind", device.Name);
        return writeThroughLogind(device.Name, (uint)raw);
    }

    internal static int ToPercent(int raw, int max)
        => max <= 0 ? -1 : (int)Math.Round(Math.Clamp(raw, 0, max) * 100.0 / max);

    /// <summary>Maps percent to a raw value. Never returns 0 for a non-zero request, since many panels switch off at raw 0.</summary>
    internal static int ToRaw(int percent, int max)
    {
        if (max <= 0)
            return 0;

        var raw = (int)Math.Round(percent * (double)max / 100.0);
        return Math.Clamp(Math.Max(raw, 1), 1, max);
    }

    private BacklightDevice? FindDevice()
    {
        BacklightDevice? best = null;
        var bestRank = int.MaxValue;
        foreach (var name in sysfs.ListDirectories(BacklightRoot))
        {
            var path = $"{BacklightRoot}/{name}";
            var max = ReadInt($"{path}/max_brightness");
            if (max is null or <= 0)
                continue;

            // Kernel convention: prefer firmware, then platform, then raw controls.
            var rank = sysfs.ReadText($"{path}/type") switch
            {
                "firmware" => 0,
                "platform" => 1,
                "raw" => 2,
                _ => 3
            };

            if (rank >= bestRank)
                continue;

            best = new BacklightDevice(name, path, max.Value);
            bestRank = rank;
        }

        return best;
    }

    private int? ReadInt(string path)
        => int.TryParse(sysfs.ReadText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static bool TryWriteSysfs(string path, string value)
    {
        try
        {
            File.WriteAllText(path, value);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record BacklightDevice(string Name, string Path, int Max);
}
