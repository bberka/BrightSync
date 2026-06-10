using Xunit;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using System;
using System.Collections.Generic;

namespace BrightSync.Tests;

public class EngineTests
{
    [Fact]
    public void TestAutoBrightnessCurveEvaluator_EmptyPoints()
    {
        var points = new List<AutoBrightnessControlPoint>();
        var result = AutoBrightnessCurveEvaluator.Evaluate(points, TimeSpan.FromHours(12));
        Assert.Equal(50, result);
    }

    [Fact]
    public void TestAutoBrightnessCurveEvaluator_SinglePoint()
    {
        var points = new List<AutoBrightnessControlPoint>
        {
            new AutoBrightnessControlPoint { MinuteOfDay = 0, Brightness = 75 }
        };
        var result = AutoBrightnessCurveEvaluator.Evaluate(points, TimeSpan.FromHours(12));
        Assert.Equal(75, result);
    }

    [Fact]
    public void TestAutoBrightnessCurveEvaluator_Interpolate()
    {
        // 06:00 -> 20%
        // 12:00 -> 80%
        // At 09:00, progress should be 50% between 06:00 and 12:00.
        // With cosine easing, eased progress at 50% is 0.5.
        // So target brightness should be exactly 20 + (80 - 20) * 0.5 = 50%.
        var points = new List<AutoBrightnessControlPoint>
        {
            new AutoBrightnessControlPoint { MinuteOfDay = 360, Brightness = 20 },
            new AutoBrightnessControlPoint { MinuteOfDay = 720, Brightness = 80 }
        };

        var result = AutoBrightnessCurveEvaluator.Evaluate(points, TimeSpan.FromHours(9));
        Assert.Equal(50, result);
    }

    [Fact]
    public void TestCalculateTarget_Normal()
    {
        var configManager = new ConfigManager();
        configManager.Config.IdleReductionEnabled = false;
        configManager.Config.EnergySaverReductionEnabled = false;
        configManager.Config.EyeProtectionEnabled = false;
        configManager.Config.BrightnessBoostEnabled = false;

        using var watcher = new InternalBrightnessWatcher();
        using var ddc = new DdcCiService(configManager);
        using var engine = new BrightSyncEngine(ddc, watcher, configManager);

        engine.ApplyAutomaticBrightness(50); // Set master brightness to 50

        var profile = new MonitorProfile
        {
            MinBrightness = 10,
            MaxBrightness = 90,
            Multiplier = 1.0
        };

        var target = engine.CalculateTarget("Monitor1", profile);
        Assert.Equal(50, target);
    }

    [Fact]
    public void TestCalculateTarget_MultiplierAndClamp()
    {
        var configManager = new ConfigManager();
        configManager.Config.IdleReductionEnabled = false;
        configManager.Config.EnergySaverReductionEnabled = false;
        configManager.Config.EyeProtectionEnabled = false;
        configManager.Config.BrightnessBoostEnabled = false;

        using var watcher = new InternalBrightnessWatcher();
        using var ddc = new DdcCiService(configManager);
        using var engine = new BrightSyncEngine(ddc, watcher, configManager);

        engine.ApplyAutomaticBrightness(50);

        var profileHigh = new MonitorProfile
        {
            MinBrightness = 10,
            MaxBrightness = 90,
            Multiplier = 1.5 // 50 * 1.5 = 75
        };

        var profileClamped = new MonitorProfile
        {
            MinBrightness = 10,
            MaxBrightness = 60,
            Multiplier = 1.5 // 50 * 1.5 = 75 -> clamped to 60
        };

        Assert.Equal(75, engine.CalculateTarget("Monitor1", profileHigh));
        Assert.Equal(60, engine.CalculateTarget("Monitor2", profileClamped));
    }

    [Fact]
    public void TestCalculateTarget_EyeProtectionAndBoost()
    {
        var configManager = new ConfigManager();
        configManager.Config.IdleReductionEnabled = false;
        configManager.Config.EnergySaverReductionEnabled = false;

        using var watcher = new InternalBrightnessWatcher();
        using var ddc = new DdcCiService(configManager);
        using var engine = new BrightSyncEngine(ddc, watcher, configManager);

        engine.ApplyAutomaticBrightness(50);

        var profile = new MonitorProfile
        {
            MinBrightness = 10,
            MaxBrightness = 90,
            Multiplier = 1.0
        };

        // Eye Protection Enabled (-20%)
        configManager.Config.EyeProtectionEnabled = true;
        configManager.Config.EyeProtectionReductionPercent = 20;
        configManager.Config.BrightnessBoostEnabled = false;
        Assert.Equal(30, engine.CalculateTarget("Monitor1", profile));

        // Brightness Boost Enabled (+20%)
        configManager.Config.EyeProtectionEnabled = false;
        configManager.Config.BrightnessBoostEnabled = true;
        configManager.Config.BrightnessBoostPercent = 20;
        Assert.Equal(70, engine.CalculateTarget("Monitor1", profile));
    }

    [Fact]
    public void TestCalculateTarget_IdleReduction()
    {
        var configManager = new ConfigManager();
        configManager.Config.EnergySaverReductionEnabled = false;
        configManager.Config.EyeProtectionEnabled = false;
        configManager.Config.BrightnessBoostEnabled = false;

        using var watcher = new InternalBrightnessWatcher();
        using var ddc = new DdcCiService(configManager);
        using var engine = new BrightSyncEngine(ddc, watcher, configManager);

        engine.ApplyAutomaticBrightness(60);

        var profile = new MonitorProfile
        {
            MinBrightness = 15,
            MaxBrightness = 90,
            Multiplier = 1.0
        };

        // Idle reduction active (50%)
        configManager.Config.IdleReductionEnabled = true;
        configManager.Config.IdleReductionPercent = 50;
        configManager.Config.IdleReductionToMinimum = false;
        engine.SetIdleReductionActive(true);

        Assert.Equal(30, engine.CalculateTarget("Monitor1", profile));

        // Idle reduction to minimum
        configManager.Config.IdleReductionToMinimum = true;
        Assert.Equal(15, engine.CalculateTarget("Monitor1", profile));
    }
}