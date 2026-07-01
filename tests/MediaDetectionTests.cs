using Xunit;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using System;

namespace BrightSync.Tests;

public class MediaDetectionTests
{
    [Fact]
    public void TestIdleDimming_WhenMediaNotPlaying_Dims()
    {
        var configManager = new ConfigManager();
        configManager.Config.IdleReductionEnabled = true;
        configManager.Config.IdleTimeoutMinutes = 2;
        configManager.Config.IdleIgnoreMediaPlayback = true;

        using var watcher = new InternalBrightnessWatcher();
        using var ddc = new DdcCiService(configManager);
        using var engine = new BrightSyncEngine(ddc, watcher, configManager);
        using var idleService = new IdleReductionService(engine, configManager);

        // Mock 3 minutes of idle time (above threshold of 2 minutes)
        idleService.IdleDurationOverride = () => TimeSpan.FromMinutes(3);
        // Mock media NOT playing
        idleService.MediaPlaybackOverride = () => false;

        idleService.ReevaluateNow();

        Assert.True(engine.IsIdleReductionActive);
    }

    [Fact]
    public void TestIdleDimming_WhenMediaPlaying_DoesNotDim()
    {
        var configManager = new ConfigManager();
        configManager.Config.IdleReductionEnabled = true;
        configManager.Config.IdleTimeoutMinutes = 2;
        configManager.Config.IdleIgnoreMediaPlayback = true;

        using var watcher = new InternalBrightnessWatcher();
        using var ddc = new DdcCiService(configManager);
        using var engine = new BrightSyncEngine(ddc, watcher, configManager);
        using var idleService = new IdleReductionService(engine, configManager);

        // Mock 3 minutes of idle time (above threshold of 2 minutes)
        idleService.IdleDurationOverride = () => TimeSpan.FromMinutes(3);
        // Mock media IS playing
        idleService.MediaPlaybackOverride = () => true;

        idleService.ReevaluateNow();

        Assert.False(engine.IsIdleReductionActive);
    }

    [Fact]
    public void TestIdleDimming_WhenIgnoreMediaDisabled_Dims()
    {
        var configManager = new ConfigManager();
        configManager.Config.IdleReductionEnabled = true;
        configManager.Config.IdleTimeoutMinutes = 2;
        configManager.Config.IdleIgnoreMediaPlayback = false; // Ignore media is OFF

        using var watcher = new InternalBrightnessWatcher();
        using var ddc = new DdcCiService(configManager);
        using var engine = new BrightSyncEngine(ddc, watcher, configManager);
        using var idleService = new IdleReductionService(engine, configManager);

        // Mock 3 minutes of idle time (above threshold of 2 minutes)
        idleService.IdleDurationOverride = () => TimeSpan.FromMinutes(3);
        // Mock media IS playing, but should be ignored because IdleIgnoreMediaPlayback = false
        idleService.MediaPlaybackOverride = () => true;

        idleService.ReevaluateNow();

        Assert.True(engine.IsIdleReductionActive);
    }
}
