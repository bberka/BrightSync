using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using BrightSync.UI;

namespace BrightSync.Tests;

public class TrayMenuViewModelTests : IDisposable
{
    private readonly BrightnessBoostService _brightnessBoost;
    private readonly ConfigManager _config;
    private readonly DdcCiService _ddc;
    private readonly BrightSyncEngine _engine;
    private readonly EyeProtectionService _eyeProtection;
    private readonly InternalBrightnessWatcher _watcher;
    private int _exitCount;

    private int _openSettingsCount;
    private int _quickPopupCount;
    private int _refreshCount;

    public TrayMenuViewModelTests()
    {
        _config = new ConfigManager();
        _config.Config.EyeProtectionEnabled = false;
        _config.Config.BrightnessBoostEnabled = false;
        _ddc = new DdcCiService(_config);
        _watcher = new InternalBrightnessWatcher();
        _engine = new BrightSyncEngine(_ddc, _watcher, _config);

        _eyeProtection = new EyeProtectionService(_engine, _config);
        _brightnessBoost = new BrightnessBoostService(_engine, _config);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _watcher.Dispose();
        _ddc.Dispose();
    }

    private TrayMenuViewModel CreateVm() => new(
        _engine,
        _eyeProtection,
        _brightnessBoost,
        () => _openSettingsCount++,
        () => _exitCount++,
        () => _quickPopupCount++,
        () => _refreshCount++);

    [Fact]
    public void OpenSettingsCommand_InvokesCallback()
    {
        var vm = CreateVm();
        vm.OpenSettingsCommand.Execute(null);
        Assert.Equal(1, _openSettingsCount);
    }

    [Fact]
    public void ExitCommand_InvokesCallback()
    {
        var vm = CreateVm();
        vm.ExitCommand.Execute(null);
        Assert.Equal(1, _exitCount);
    }

    [Fact]
    public void ToggleQuickPopupCommand_InvokesCallback()
    {
        var vm = CreateVm();
        vm.ToggleQuickPopupCommand.Execute(null);
        Assert.Equal(1, _quickPopupCount);
    }

    [Fact]
    public void RefreshMonitorsCommand_InvokesCallback()
    {
        var vm = CreateVm();
        vm.RefreshMonitorsCommand.Execute(null);
        Assert.Equal(1, _refreshCount);
    }

    [Fact]
    public void EyeProtectionToggleHeader_ShowsCheckWhenEnabled()
    {
        _config.Config.EyeProtectionEnabled = true;
        var vm = CreateVm();
        Assert.Equal("✓ Toggle Eye Protection", vm.EyeProtectionToggleHeader);
    }

    [Fact]
    public void EyeProtectionToggleHeader_HidesCheckWhenDisabled()
    {
        _config.Config.EyeProtectionEnabled = false;
        var vm = CreateVm();
        Assert.Equal("Toggle Eye Protection", vm.EyeProtectionToggleHeader);
    }

    [Fact]
    public void BrightnessBoostToggleHeader_ShowsCheckWhenEnabled()
    {
        _config.Config.BrightnessBoostEnabled = true;
        var vm = CreateVm();
        Assert.Equal("✓ Toggle Brightness Boost", vm.BrightnessBoostToggleHeader);
    }

    [Fact]
    public void BrightnessBoostToggleHeader_HidesCheckWhenDisabled()
    {
        _config.Config.BrightnessBoostEnabled = false;
        var vm = CreateVm();
        Assert.Equal("Toggle Brightness Boost", vm.BrightnessBoostToggleHeader);
    }

    [Fact]
    public void ToggleEyeProtectionCommand_DisablesWhenEnabled()
    {
        _config.Config.EyeProtectionEnabled = true;
        var vm = CreateVm();
        Assert.True(vm.EyeProtectionEnabled);
        vm.ToggleEyeProtectionCommand.Execute(null);
        Assert.False(_config.Config.EyeProtectionEnabled);
    }

    [Fact]
    public void ToggleEyeProtectionCommand_EnablesWhenDisabled()
    {
        _config.Config.EyeProtectionEnabled = false;
        var vm = CreateVm();
        Assert.False(vm.EyeProtectionEnabled);
        vm.ToggleEyeProtectionCommand.Execute(null);
        Assert.True(_config.Config.EyeProtectionEnabled);
    }

    [Fact]
    public void ToggleBrightnessBoostCommand_DisablesWhenEnabled()
    {
        _config.Config.BrightnessBoostEnabled = true;
        var vm = CreateVm();
        Assert.True(vm.BrightnessBoostEnabled);
        vm.ToggleBrightnessBoostCommand.Execute(null);
        Assert.False(_config.Config.BrightnessBoostEnabled);
    }

    [Fact]
    public void ToggleBrightnessBoostCommand_EnablesWhenDisabled()
    {
        _config.Config.BrightnessBoostEnabled = false;
        var vm = CreateVm();
        Assert.False(vm.BrightnessBoostEnabled);
        vm.ToggleBrightnessBoostCommand.Execute(null);
        Assert.True(_config.Config.BrightnessBoostEnabled);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(24)]
    public void SetEyeProtectionPresetCommand_EnablesWithDuration(int hours)
    {
        _config.Config.EyeProtectionEnabled = false;
        _config.Config.EyeProtectionDefaultDurationHours = hours;
        var vm = CreateVm();

        var command = vm.SetEyeProtectionPresetCommands[hours];
        Assert.NotNull(command);
        command.Execute(null);

        Assert.True(_config.Config.EyeProtectionEnabled);
        var endUtc = _eyeProtection.EndTimeUtc;
        Assert.NotNull(endUtc);
        var expected = DateTime.UtcNow.AddHours(hours);
        var delta = (expected - endUtc.Value).Duration();
        Assert.True(delta < TimeSpan.FromMinutes(2), $"End time {endUtc} too far from expected {expected}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(24)]
    public void SetBrightnessBoostPresetCommand_EnablesWithDuration(int hours)
    {
        _config.Config.BrightnessBoostEnabled = false;
        _config.Config.BrightnessBoostDefaultDurationHours = hours;
        var vm = CreateVm();

        var command = vm.SetBrightnessBoostPresetCommands[hours];
        Assert.NotNull(command);
        command.Execute(null);

        Assert.True(_config.Config.BrightnessBoostEnabled);
        var endUtc = _brightnessBoost.EndTimeUtc;
        Assert.NotNull(endUtc);
        var expected = DateTime.UtcNow.AddHours(hours);
        var delta = (expected - endUtc.Value).Duration();
        Assert.True(delta < TimeSpan.FromMinutes(2), $"End time {endUtc} too far from expected {expected}");
    }

    [Fact]
    public void PresetHours_ContainsExpectedValues()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 8, 12, 24 }, TrayMenuViewModel.PresetHours);
    }

    [Fact]
    public void MasterBrightness_ReturnsEngineValue()
    {
        var vm = CreateVm();
        Assert.Equal(_engine.MasterBrightness, vm.MasterBrightness);
    }
}