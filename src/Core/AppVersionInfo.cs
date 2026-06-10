using System.IO;
using System.Reflection;

namespace BrightSync.Core;

public static class AppVersionInfo
{
    public static string GetDisplayTitle()
    {
        var version = GetCurrentVersion();
        return version is null
            ? "BrightSync"
            : $"BrightSync v{version}";
    }

    public static Version? GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+', 2)[0];

        var parsedInformationalVersion = TryParseVersion(informationalVersion);
        if (parsedInformationalVersion is not null)
        {
            return parsedInformationalVersion;
        }

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var parsedFileVersion = TryParseVersion(fileVersion);
        if (parsedFileVersion is not null && parsedFileVersion != new Version(1, 0, 0, 0))
        {
            return parsedFileVersion;
        }

        var versionFilePath = Path.Combine(AppContext.BaseDirectory, "VERSION");
        if (File.Exists(versionFilePath))
        {
            return TryParseVersion(File.ReadAllText(versionFilePath).Trim());
        }

        return null;
    }

    private static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        return Version.TryParse(normalized, out var version) ? version : null;
    }
}