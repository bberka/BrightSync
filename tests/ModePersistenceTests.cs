using BrightSync.Cli;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using BrightSync.UI.ViewModels;

namespace BrightSync.Tests;

public sealed class ModePersistenceTests
{
    [Fact]
    public void AutoBrightnessService_SetEnabled_persists_for_reload()
    {
        using var context = new TestContext();
        var config = context.CreateConfig();
        var engine = new FakeBrightnessEngine();
        using var service = new AutoBrightnessService(engine, config);

        service.SetEnabled(true);

        Assert.True(service.IsEnabled);
        Assert.True(context.CreateConfig().Config.AutoBrightness.Enabled);

        service.SetEnabled(false);

        Assert.False(service.IsEnabled);
        Assert.False(context.CreateConfig().Config.AutoBrightness.Enabled);
    }

    [Fact]
    public void QuickBrightnessViewModel_auto_toggle_persists_for_reload()
    {
        using var context = new TestContext();
        var config = context.CreateConfig();
        var engine = new FakeBrightnessEngine();
        using var autoBrightness = new AutoBrightnessService(engine, config);
        using var eyeProtection = new EyeProtectionService(engine, config);
        using var brightnessBoost = new BrightnessBoostService(engine, config);
        using var viewModel = new QuickBrightnessViewModel(
            engine,
            autoBrightness,
            eyeProtection,
            brightnessBoost,
            static () => [],
            config,
            () => { },
            static action => action());

        viewModel.AutoBrightnessEnabled = true;

        Assert.True(context.CreateConfig().Config.AutoBrightness.Enabled);

        viewModel.AutoBrightnessEnabled = false;

        Assert.False(context.CreateConfig().Config.AutoBrightness.Enabled);
    }

    [Fact]
    public async Task ResidentCommandHandler_auto_toggle_persists_for_reload()
    {
        using var context = new TestContext();
        var config = context.CreateConfig();
        var engine = new FakeBrightnessEngine();
        using var autoBrightness = new AutoBrightnessService(engine, config);
        using var eyeProtection = new EyeProtectionService(engine, config);
        using var brightnessBoost = new BrightnessBoostService(engine, config);
        var handler = new ResidentCommandHandler(
            engine,
            autoBrightness,
            eyeProtection,
            brightnessBoost,
            () => { },
            () => { },
            () => { },
            static (handler, _) => Task.FromResult(handler()));

        var enabled = await handler.HandleAsync(
            new CommandRequest { CommandType = AppCommandType.AutoOn },
            CancellationToken.None);

        Assert.True(enabled.Success);
        Assert.True(context.CreateConfig().Config.AutoBrightness.Enabled);

        var disabled = await handler.HandleAsync(
            new CommandRequest { CommandType = AppCommandType.AutoOff },
            CancellationToken.None);

        Assert.True(disabled.Success);
        Assert.False(context.CreateConfig().Config.AutoBrightness.Enabled);
    }

    [Fact]
    public void EyeProtectionService_valid_enable_and_disable_round_trip_through_config()
    {
        using var context = new TestContext();
        var config = context.CreateConfig();
        var engine = new FakeBrightnessEngine();
        using var service = new EyeProtectionService(engine, config);

        service.SetEnabled(true, 24);

        var enabledEndTime = Assert.IsType<DateTime>(service.EndTimeUtc);
        Assert.True(enabledEndTime > DateTime.UtcNow.AddHours(23));
        var reloadedEnabled = context.CreateConfig().Config;
        Assert.True(reloadedEnabled.EyeProtectionEnabled);
        Assert.Equal(enabledEndTime, reloadedEnabled.EyeProtectionEndUtc);

        service.Dispose();
        var restoredConfig = context.CreateConfig();
        using var restoredService = new EyeProtectionService(engine, restoredConfig);
        restoredService.Start();

        Assert.True(restoredService.IsEnabled);
        Assert.Equal(enabledEndTime, restoredService.EndTimeUtc);

        restoredService.SetEnabled(false);

        var reloadedDisabled = context.CreateConfig().Config;
        Assert.False(reloadedDisabled.EyeProtectionEnabled);
        Assert.Null(reloadedDisabled.EyeProtectionEndUtc);
    }

    [Fact]
    public void BrightnessBoostService_valid_enable_and_disable_round_trip_through_config()
    {
        using var context = new TestContext();
        var config = context.CreateConfig();
        var engine = new FakeBrightnessEngine();
        using var service = new BrightnessBoostService(engine, config);

        service.SetEnabled(true, 1);

        var enabledEndTime = Assert.IsType<DateTime>(service.EndTimeUtc);
        Assert.True(enabledEndTime > DateTime.UtcNow);
        var reloadedEnabled = context.CreateConfig().Config;
        Assert.True(reloadedEnabled.BrightnessBoostEnabled);
        Assert.Equal(enabledEndTime, reloadedEnabled.BrightnessBoostEndUtc);

        service.Dispose();
        var restoredConfig = context.CreateConfig();
        using var restoredService = new BrightnessBoostService(engine, restoredConfig);
        restoredService.Start();

        Assert.True(restoredService.IsEnabled);
        Assert.Equal(enabledEndTime, restoredService.EndTimeUtc);

        restoredService.SetEnabled(false);

        var reloadedDisabled = context.CreateConfig().Config;
        Assert.False(reloadedDisabled.BrightnessBoostEnabled);
        Assert.Null(reloadedDisabled.BrightnessBoostEndUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(25)]
    [InlineData(int.MaxValue)]
    public void EyeProtectionService_rejects_invalid_duration_without_mutating_state_or_file(int hours)
    {
        using var context = new TestContext();
        var config = context.CreateConfig();
        config.Save();
        var originalJson = File.ReadAllText(context.ConfigPath);
        var engine = new FakeBrightnessEngine();
        using var service = new EyeProtectionService(engine, config);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetEnabled(true, hours));

        Assert.False(service.IsEnabled);
        Assert.Null(service.EndTimeUtc);
        Assert.Equal(originalJson, File.ReadAllText(context.ConfigPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(25)]
    [InlineData(int.MaxValue)]
    public void BrightnessBoostService_rejects_invalid_duration_without_mutating_state_or_file(int hours)
    {
        using var context = new TestContext();
        var config = context.CreateConfig();
        config.Save();
        var originalJson = File.ReadAllText(context.ConfigPath);
        var engine = new FakeBrightnessEngine();
        using var service = new BrightnessBoostService(engine, config);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetEnabled(true, hours));

        Assert.False(service.IsEnabled);
        Assert.Null(service.EndTimeUtc);
        Assert.Equal(originalJson, File.ReadAllText(context.ConfigPath));
    }

    [Fact]
    public void EyeProtectionService_rejects_unrepresentable_expiry_without_mutating_state_or_file()
    {
        using var context = new TestContext();
        var config = context.CreateConfig();
        config.Save();
        var originalJson = File.ReadAllText(context.ConfigPath);
        var engine = new FakeBrightnessEngine();
        using var service = new EyeProtectionService(
            engine,
            config,
            () => DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));

        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetEnabled(true, 1));

        Assert.False(service.IsEnabled);
        Assert.Null(service.EndTimeUtc);
        Assert.Equal(originalJson, File.ReadAllText(context.ConfigPath));
    }

    [Fact]
    public void BrightnessBoostService_rejects_unrepresentable_expiry_without_mutating_state_or_file()
    {
        using var context = new TestContext();
        var config = context.CreateConfig();
        config.Save();
        var originalJson = File.ReadAllText(context.ConfigPath);
        var engine = new FakeBrightnessEngine();
        using var service = new BrightnessBoostService(
            engine,
            config,
            () => DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));

        Assert.Throws<ArgumentOutOfRangeException>(() => service.SetEnabled(true, 1));

        Assert.False(service.IsEnabled);
        Assert.Null(service.EndTimeUtc);
        Assert.Equal(originalJson, File.ReadAllText(context.ConfigPath));
    }

    [Fact]
    public void EyeProtectionService_invalid_duration_does_not_disable_active_brightness_boost()
    {
        using var context = new TestContext();
        var config = context.CreateConfig();
        var engine = new FakeBrightnessEngine();
        using var eyeProtection = new EyeProtectionService(engine, config);
        using var brightnessBoost = new BrightnessBoostService(engine, config);
        eyeProtection.SetBrightnessBoostService(brightnessBoost);
        brightnessBoost.SetEyeProtectionService(eyeProtection);

        brightnessBoost.SetEnabled(true, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => eyeProtection.SetEnabled(true, 25));

        Assert.False(eyeProtection.IsEnabled);
        Assert.True(brightnessBoost.IsEnabled);
        Assert.True(context.CreateConfig().Config.BrightnessBoostEnabled);
    }

    [Fact]
    public void BrightnessBoostService_invalid_duration_does_not_disable_active_eye_protection()
    {
        using var context = new TestContext();
        var config = context.CreateConfig();
        var engine = new FakeBrightnessEngine();
        using var eyeProtection = new EyeProtectionService(engine, config);
        using var brightnessBoost = new BrightnessBoostService(engine, config);
        eyeProtection.SetBrightnessBoostService(brightnessBoost);
        brightnessBoost.SetEyeProtectionService(eyeProtection);

        eyeProtection.SetEnabled(true, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => brightnessBoost.SetEnabled(true, 25));

        Assert.True(eyeProtection.IsEnabled);
        Assert.False(brightnessBoost.IsEnabled);
        Assert.True(context.CreateConfig().Config.EyeProtectionEnabled);
    }

    private sealed class FakeBrightnessEngine : IBrightnessEngineOperations
    {
        public event EventHandler<int>? MasterBrightnessChanged;
        public event EventHandler? TargetsChanged;

        public int MasterBrightness { get; private set; } = 50;
        public bool IsIdleReductionActive => false;

        public bool ApplyAutomaticBrightness(int brightness)
        {
            MasterBrightness = brightness;
            MasterBrightnessChanged?.Invoke(this, brightness);
            return true;
        }

        public bool TrySetUserBrightness(int brightness)
        {
            MasterBrightness = brightness;
            MasterBrightnessChanged?.Invoke(this, brightness);
            return true;
        }

        public int CalculateTarget(string monitorDeviceName, MonitorProfile profile)
            => Math.Clamp(MasterBrightness, profile.MinBrightness, profile.MaxBrightness);

        public void ForceSync()
            => TargetsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class TestContext : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "BrightSyncTests",
            Guid.NewGuid().ToString("N"));

        public string ConfigPath => Path.Combine(_directory, "config.json");

        public ConfigManager CreateConfig()
        {
            Directory.CreateDirectory(_directory);
            return new ConfigManager(ConfigPath, _ => { });
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}
