using BrightSync.Core;

namespace BrightSync.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void Directories_AreRootedAndNamedAfterTheApp()
    {
        Assert.True(Path.IsPathRooted(AppPaths.RoamingDirectory));
        Assert.True(Path.IsPathRooted(AppPaths.LocalDirectory));
        Assert.Equal("BrightSync", Path.GetFileName(AppPaths.RoamingDirectory));
        Assert.Equal("BrightSync", Path.GetFileName(AppPaths.LocalDirectory));
    }

    [Fact]
    public void Resolve_CreatesAMissingXdgConfigHomeInsteadOfFallingBackToTheWorkingDirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "XDG_CONFIG_HOME only applies on Linux.");

        var missing = Path.Combine(Path.GetTempPath(), "brightsync-xdg-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", missing);
            Assert.False(Directory.Exists(missing));

            var resolved = AppPaths.Resolve(Environment.SpecialFolder.ApplicationData);

            Assert.Equal(Path.Combine(missing, "BrightSync"), resolved);
            Assert.True(Directory.Exists(missing));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            if (Directory.Exists(missing))
                Directory.Delete(missing, recursive: true);
        }
    }
}
