namespace BrightSync.Core;

/// <summary>
/// Per-user directories BrightSync writes to. <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
/// returns an empty string on Linux when the folder does not exist yet (a fresh or minimal account has no
/// <c>~/.config</c>), which would silently put config and logs under the current directory.
/// <see cref="Environment.SpecialFolderOption.Create"/> makes the folder instead.
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "BrightSync";

    /// <summary>Roaming data: <c>%APPDATA%\BrightSync</c> on Windows, <c>~/.config/BrightSync</c> on Linux.</summary>
    public static string RoamingDirectory
        => Resolve(Environment.SpecialFolder.ApplicationData);

    /// <summary>Machine-local data: <c>%LOCALAPPDATA%\BrightSync</c> on Windows, <c>~/.local/share/BrightSync</c> on Linux.</summary>
    public static string LocalDirectory
        => Resolve(Environment.SpecialFolder.LocalApplicationData);

    internal static string Resolve(Environment.SpecialFolder folder)
    {
        var root = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrWhiteSpace(root))
        {
            // No usable home directory (for example a service account without HOME): stay out of the
            // working directory and use a per-user temp location instead.
            root = Path.Combine(Path.GetTempPath(), $"brightsync-{Environment.UserName}");
        }

        return Path.Combine(root, AppFolderName);
    }
}
