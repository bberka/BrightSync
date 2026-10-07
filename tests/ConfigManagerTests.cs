using System.Text.Json;
using BrightSync.Core.Config;

namespace BrightSync.Tests;

public sealed class ConfigManagerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "BrightSyncTests",
        Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_tempDirectory, "config.json");

    [Fact]
    public void Save_creates_an_isolated_default_configuration_that_can_be_reloaded()
    {
        var manager = CreateManager();

        Assert.Equal(-1, manager.Config.MasterBrightness);
        Assert.Equal(
            AutoBrightnessSettings.GetDefaultPointMinutes(),
            manager.Config.AutoBrightness.Curve.Select(point => point.MinuteOfDay));

        manager.Save();

        Assert.True(File.Exists(ConfigPath));
        var reloaded = CreateManager();
        Assert.Equal(-1, reloaded.Config.MasterBrightness);
        Assert.Equal(
            AutoBrightnessSettings.GetDefaultPointMinutes(),
            reloaded.Config.AutoBrightness.Curve.Select(point => point.MinuteOfDay));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(100)]
    public void Save_accepts_master_brightness_boundaries(int brightness)
    {
        var manager = CreateManager();
        manager.Config.MasterBrightness = brightness;

        manager.Save();

        Assert.Equal(brightness, CreateManager().Config.MasterBrightness);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(101)]
    public void Save_rejects_invalid_master_brightness_without_changing_the_live_file(int brightness)
    {
        var manager = CreateManager();
        manager.Config.MasterBrightness = 50;
        manager.Save();
        var originalJson = File.ReadAllText(ConfigPath);

        manager.Config.MasterBrightness = brightness;

        Assert.Throws<InvalidDataException>(() => manager.Save());
        Assert.Equal(originalJson, File.ReadAllText(ConfigPath));
        Assert.Equal(50, CreateManager().Config.MasterBrightness);
    }

    [Fact]
    public void Save_rejects_monitor_profiles_outside_the_supported_boundaries()
    {
        var manager = CreateManager();
        var profile = manager.GetOrCreateProfile("DISPLAY1");
        profile.MinBrightness = 101;

        Assert.Throws<InvalidDataException>(() => manager.Save());
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task Concurrent_saves_from_managers_leave_valid_live_and_backup_files()
    {
        var initial = CreateManager();
        initial.Config.MasterBrightness = 50;
        initial.Save();

        var saves = Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            var manager = CreateManager();
            manager.Config.MasterBrightness = index;
            manager.GetOrCreateProfile($"DISPLAY{index}");
            manager.Save();
        }));

        await Task.WhenAll(saves);

        AssertValidJson(ConfigPath);
        AssertValidJson(ConfigPath + ".bak");
        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp"));

        var reloaded = CreateManager();
        Assert.InRange(reloaded.Config.MasterBrightness, 0, 15);
        Assert.NotEmpty(reloaded.Config.Monitors);
    }

    [Fact]
    public void Load_recovers_from_a_valid_backup_without_replacing_the_corrupt_live_file()
    {
        var manager = CreateManager();
        manager.Config.MasterBrightness = 23;
        manager.Save();
        manager.Config.MasterBrightness = 77;
        manager.Save();

        const string corruptJson = "{ not valid json";
        File.WriteAllText(ConfigPath, corruptJson);

        var recovered = CreateManager();

        Assert.Equal(23, recovered.Config.MasterBrightness);
        Assert.Equal(corruptJson, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Load_uses_defaults_when_live_and_backup_files_are_unusable()
    {
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(ConfigPath, "{ not valid json");
        File.WriteAllText(ConfigPath + ".bak", "also not valid json");

        var manager = CreateManager();

        Assert.Equal(-1, manager.Config.MasterBrightness);
        Assert.NotEmpty(manager.Config.AutoBrightness.Curve);
    }

    [Fact]
    public void Save_refuses_to_replace_a_valid_live_file_after_a_load_failure()
    {
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(ConfigPath, "{ not valid json");
        var manager = CreateManager();
        const string replacementJson = "{\"MasterBrightness\":91}";
        File.WriteAllText(ConfigPath, replacementJson);

        manager.Config.MasterBrightness = 12;

        Assert.Throws<InvalidOperationException>(() => manager.Save());
        Assert.Equal(replacementJson, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Save_applies_startup_registration_only_for_the_first_save_or_a_changed_value()
    {
        var appliedValues = new List<bool>();
        var manager = new ConfigManager(ConfigPath, value => appliedValues.Add(value));

        manager.Save();
        manager.Save();
        manager.Config.MasterBrightness = 40;
        manager.Save();
        manager.Config.StartWithWindows = true;
        manager.Save();
        manager.Save();

        Assert.Equal([true], appliedValues);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private ConfigManager CreateManager()
        => new(ConfigPath, _ => { });

    private static void AssertValidJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }
}
