using Xunit;
using BrightSync.UI.ViewModels;
using BrightSync.Core.Monitors;
using BrightSync.Core.Config;
using BrightSync.Core.Brightness;
using System.Collections.Generic;

namespace BrightSync.Tests;

public class AdvancedControlsTests
{
    [Fact]
    public void TestParsedCapabilities_ValidString()
    {
        var configManager = new ConfigManager();
        configManager.Config.IdleReductionEnabled = false;
        configManager.Config.EnergySaverReductionEnabled = false;
        configManager.Config.EyeProtectionEnabled = false;
        configManager.Config.BrightnessBoostEnabled = false;

        using var watcher = new InternalBrightnessWatcher();
        using var ddc = new DdcCiService(configManager);
        using var engine = new BrightSyncEngine(ddc, watcher, configManager);

        var monitor = new DdcMonitor
        {
            DeviceName = "TestMonitor",
            RawCapabilitiesString = "(prot(monitor)type(LCD)model(SyncMaster)vcp(02 04 10 87 E2 FD FE))"
        };
        var profile = new MonitorProfile();

        var viewModel = new MonitorRowViewModel(monitor, profile, engine);

        var parsed = viewModel.ParsedCapabilities;

        Assert.Equal(7, parsed.Count);
        Assert.Contains("0x02", parsed);
        Assert.Contains("0x04", parsed);
        Assert.Contains("0x10", parsed);
        Assert.Contains("0x87", parsed);
        Assert.Contains("0xE2", parsed);
        Assert.Contains("0xFD", parsed);
        Assert.Contains("0xFE", parsed);
    }

    [Fact]
    public void TestParsedCapabilities_EmptyString()
    {
        var configManager = new ConfigManager();
        configManager.Config.IdleReductionEnabled = false;
        configManager.Config.EnergySaverReductionEnabled = false;
        configManager.Config.EyeProtectionEnabled = false;
        configManager.Config.BrightnessBoostEnabled = false;

        using var watcher = new InternalBrightnessWatcher();
        using var ddc = new DdcCiService(configManager);
        using var engine = new BrightSyncEngine(ddc, watcher, configManager);

        var monitor = new DdcMonitor
        {
            DeviceName = "TestMonitor",
            RawCapabilitiesString = ""
        };
        var profile = new MonitorProfile();

        var viewModel = new MonitorRowViewModel(monitor, profile, engine);

        var parsed = viewModel.ParsedCapabilities;

        Assert.Empty(parsed);
    }

    [Fact]
    public void TestParsedCapabilities_NoVcpSection()
    {
        var configManager = new ConfigManager();
        configManager.Config.IdleReductionEnabled = false;
        configManager.Config.EnergySaverReductionEnabled = false;
        configManager.Config.EyeProtectionEnabled = false;
        configManager.Config.BrightnessBoostEnabled = false;

        using var watcher = new InternalBrightnessWatcher();
        using var ddc = new DdcCiService(configManager);
        using var engine = new BrightSyncEngine(ddc, watcher, configManager);

        var monitor = new DdcMonitor
        {
            DeviceName = "TestMonitor",
            RawCapabilitiesString = "(prot(monitor)type(LCD)model(SyncMaster))"
        };
        var profile = new MonitorProfile();

        var viewModel = new MonitorRowViewModel(monitor, profile, engine);

        var parsed = viewModel.ParsedCapabilities;

        Assert.Empty(parsed);
    }

    [Fact]
    public void TestSupportedVcpCodesList_Filtering()
    {
        var configManager = new ConfigManager();
        configManager.Config.IdleReductionEnabled = false;
        configManager.Config.EnergySaverReductionEnabled = false;
        configManager.Config.EyeProtectionEnabled = false;
        configManager.Config.BrightnessBoostEnabled = false;

        using var watcher = new InternalBrightnessWatcher();
        using var ddc = new DdcCiService(configManager);
        using var engine = new BrightSyncEngine(ddc, watcher, configManager);

        var monitor = new DdcMonitor
        {
            DeviceName = "TestMonitor",
            RawCapabilitiesString = "(prot(monitor)type(LCD)model(SyncMaster)vcp(10 87))"
        };
        var profile = new MonitorProfile();

        var viewModel = new MonitorRowViewModel(monitor, profile, engine);

        var list = viewModel.SupportedVcpCodesList;

        // Verify it contains Brightness (0x10) and Sharpness (0x87)
        Assert.Equal(2, list.Count);
        Assert.Contains(list, item => item.HexCode.Equals("10", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(list, item => item.HexCode.Equals("87", System.StringComparison.OrdinalIgnoreCase));

        // Verify it does NOT contain other fallback/known codes like Samsung Eye Saver Mode (0xE2)
        Assert.DoesNotContain(list, item => item.HexCode.Equals("E2", System.StringComparison.OrdinalIgnoreCase));
    }
}
