namespace BrightSync.Cli;

internal static class CommandServerPaths
{
    private static readonly string BaseDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrightSync");

    public static string MetadataFilePath => Path.Combine(BaseDirectory, "command-server.json");

    public static void EnsureDirectory()
        => Directory.CreateDirectory(BaseDirectory);
}
