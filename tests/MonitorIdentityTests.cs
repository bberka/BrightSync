using BrightSync.Core.Config;
using BrightSync.Core.Monitors;

namespace BrightSync.Tests;

public sealed class MonitorIdentityTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "BrightSyncTests",
        Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_tempDirectory, "config.json");

    [Fact]
    public void Resolve_prefers_and_normalizes_the_display_config_instance_path()
    {
        var identity = MonitorIdentityResolver.Resolve(
            @"  \\?\DISPLAY#del4141#5& abc &0&uid257_0#{4d36e96e-...}  ",
            @"MONITOR\OTHER\INSTANCE",
            @"\\.\DISPLAY2");

        Assert.Equal("edid:DEL4141|5&ABC&0&UID257_0|{4D36E96E-...}", identity);
        Assert.True(MonitorIdentityResolver.IsStableIdentity(identity));
    }

    [Fact]
    public void Resolve_uses_normalized_hardware_then_device_name_fallbacks()
    {
        var hardwareIdentity = MonitorIdentityResolver.Resolve(
            null,
            @" monitor/del4141/{4d36e96e-...} ",
            @"\\.\DISPLAY2");
        var deviceNameIdentity = MonitorIdentityResolver.Resolve(null, null, @" \\.\display2 ");

        Assert.Equal("hardware:DEL4141|{4D36E96E-...}", hardwareIdentity);
        Assert.True(MonitorIdentityResolver.IsStableIdentity(hardwareIdentity));
        Assert.Equal("display:DISPLAY2", deviceNameIdentity);
        Assert.True(MonitorIdentityResolver.IsDeviceNameFallback(deviceNameIdentity));
        Assert.Equal("display:UNKNOWN", MonitorIdentityResolver.Resolve(null, null, null));
    }

    [Fact]
    public void Duplicate_runtime_identities_get_distinct_conservative_fallbacks()
    {
        var monitors = new List<DdcMonitor>
        {
            new() { DeviceName = @"\\.\DISPLAY1", StableIdentity = "hardware:DEL4141" },
            new() { DeviceName = @"\\.\DISPLAY2", StableIdentity = "HARDWARE:DEL4141" }
        };

        MonitorIdentityResolver.EnsureUniqueIdentities(monitors);

        Assert.Equal("display:DISPLAY1|0", monitors[0].StableIdentity);
        Assert.Equal("display:DISPLAY2|1", monitors[1].StableIdentity);
        Assert.NotEqual(monitors[0].StableIdentity, monitors[1].StableIdentity);
    }

    [Fact]
    public void GetOrCreateProfile_migrates_one_exact_legacy_profile_without_changing_settings()
    {
        var manager = CreateManager();
        const string legacyKey = @"\\.\DISPLAY1";
        var legacyProfile = manager.GetOrCreateProfile(legacyKey);
        legacyProfile.Enabled = false;
        legacyProfile.MinBrightness = 13;
        legacyProfile.MaxBrightness = 87;
        legacyProfile.Multiplier = 0.8;
        legacyProfile.AssociatedColorProfile = "sRGB Color Space Profile.icm";
        legacyProfile.CustomActions.Add(new CustomVcpActionProfile
        {
            Name = "Night",
            VcpCode = 0x10,
            Value = 24
        });

        var stableKey = "edid:DEL4141|5&ABC&0&UID257_0";
        var resolved = manager.GetOrCreateProfile(legacyKey, stableKey);

        Assert.Same(legacyProfile, resolved);
        Assert.False(resolved.Enabled);
        Assert.Equal(13, resolved.MinBrightness);
        Assert.Equal(87, resolved.MaxBrightness);
        Assert.Equal(0.8, resolved.Multiplier);
        Assert.Equal("sRGB Color Space Profile.icm", resolved.AssociatedColorProfile);
        Assert.Equal((byte)0x10, Assert.Single(resolved.CustomActions).VcpCode);
        Assert.DoesNotContain(legacyKey, manager.Config.Monitors.Keys);
        Assert.Contains(stableKey, manager.Config.Monitors.Keys);

        manager.Save();
        var reloaded = CreateManager();
        var persisted = reloaded.Config.Monitors[stableKey];
        Assert.False(persisted.Enabled);
        Assert.Equal(13, persisted.MinBrightness);
        Assert.Equal(0.8, persisted.Multiplier);
        Assert.Equal((byte)0x10, Assert.Single(persisted.CustomActions).VcpCode);
    }

    [Fact]
    public void GetOrCreateProfile_migrates_the_short_DISPLAYn_legacy_spelling()
    {
        var manager = CreateManager();
        const string legacyKey = "DISPLAY1";
        var legacyProfile = manager.GetOrCreateProfile(legacyKey);
        legacyProfile.MinBrightness = 17;

        const string stableKey = "edid:DEL4141|INSTANCE-A";
        var resolved = manager.GetOrCreateProfile(@"\\.\DISPLAY1", stableKey);

        Assert.Same(legacyProfile, resolved);
        Assert.Equal(17, resolved.MinBrightness);
        Assert.DoesNotContain(legacyKey, manager.Config.Monitors.Keys);
        Assert.Contains(stableKey, manager.Config.Monitors.Keys);
    }

    [Fact]
    public void GetOrCreateProfile_does_not_overwrite_a_stable_profile_or_legacy_profile_on_collision()
    {
        var manager = CreateManager();
        const string legacyKey = @"\\.\DISPLAY1";
        const string stableKey = "edid:DEL4141|5&ABC&0&UID257_0";
        var legacyProfile = manager.GetOrCreateProfile(legacyKey);
        legacyProfile.MinBrightness = 11;

        var stableProfile = manager.GetOrCreateProfile(@"\\.\DISPLAY9", stableKey);
        stableProfile.MinBrightness = 77;

        var resolved = manager.GetOrCreateProfile(legacyKey, stableKey);

        Assert.Same(stableProfile, resolved);
        Assert.Equal(77, resolved.MinBrightness);
        Assert.Same(legacyProfile, manager.Config.Monitors[legacyKey]);
        Assert.Equal(11, manager.Config.Monitors[legacyKey].MinBrightness);
        Assert.Equal(2, manager.Config.Monitors.Count);
    }

    [Fact]
    public void GetOrCreateProfile_leaves_multiple_legacy_profiles_untouched_when_mapping_is_ambiguous()
    {
        var manager = CreateManager();
        const string firstLegacyKey = @"\\.\DISPLAY1";
        const string secondLegacyKey = @"\\.\DISPLAY2";
        var firstLegacyProfile = manager.GetOrCreateProfile(firstLegacyKey);
        var secondLegacyProfile = manager.GetOrCreateProfile(secondLegacyKey);
        firstLegacyProfile.MinBrightness = 21;
        secondLegacyProfile.MinBrightness = 62;

        const string stableKey = "edid:DEL4141|5&ABC&0&UID257_0";
        var resolved = manager.GetOrCreateProfile(firstLegacyKey, stableKey);

        Assert.NotSame(firstLegacyProfile, resolved);
        Assert.Equal(MonitorProfile.DefaultMinBrightness, resolved.MinBrightness);
        Assert.Same(firstLegacyProfile, manager.Config.Monitors[firstLegacyKey]);
        Assert.Same(secondLegacyProfile, manager.Config.Monitors[secondLegacyKey]);
        Assert.Equal(21, manager.Config.Monitors[firstLegacyKey].MinBrightness);
        Assert.Equal(62, manager.Config.Monitors[secondLegacyKey].MinBrightness);
    }

    [Fact]
    public void GetOrCreateProfile_uses_a_fallback_without_merging_case_colliding_stable_keys()
    {
        var manager = CreateManager();
        const string firstKey = "edid:DEL4141|INSTANCE-A";
        const string secondKey = "EDID:DEL4141|INSTANCE-A";
        manager.Config.Monitors[firstKey] = new MonitorProfile { MinBrightness = 14 };
        manager.Config.Monitors[secondKey] = new MonitorProfile { MinBrightness = 41 };

        var resolved = manager.GetOrCreateProfile(@"\\.\DISPLAY1", firstKey);

        Assert.DoesNotContain(resolved, new[] { manager.Config.Monitors[firstKey], manager.Config.Monitors[secondKey] });
        Assert.Equal(MonitorProfile.DefaultMinBrightness, resolved.MinBrightness);
        Assert.Equal(14, manager.Config.Monitors[firstKey].MinBrightness);
        Assert.Equal(41, manager.Config.Monitors[secondKey].MinBrightness);
    }

    [Fact]
    public void GetOrCreateProfile_keeps_disambiguated_fallbacks_separate_from_legacy_keys()
    {
        var manager = CreateManager();
        const string legacyKey = @"\\.\DISPLAY1";
        var legacyProfile = manager.GetOrCreateProfile(legacyKey);
        legacyProfile.MinBrightness = 19;
        var fallbackKey = MonitorIdentityResolver.CreateDeviceNameFallback(legacyKey, 0);

        var resolved = manager.GetOrCreateProfile(legacyKey, fallbackKey);

        Assert.NotSame(legacyProfile, resolved);
        Assert.Equal(19, manager.Config.Monitors[legacyKey].MinBrightness);
        Assert.Same(resolved, manager.Config.Monitors[fallbackKey]);
    }

    [Fact]
    public void GetOrCreateProfile_does_not_guess_a_legacy_profile_when_only_DISPLAY_identity_is_available()
    {
        var manager = CreateManager();
        const string legacyKey = @"\\.\DISPLAY1";
        var legacyProfile = manager.GetOrCreateProfile(legacyKey);
        legacyProfile.MinBrightness = 23;
        var fallbackKey = MonitorIdentityResolver.Resolve(null, null, legacyKey);

        var resolved = manager.GetOrCreateProfile(legacyKey, fallbackKey);

        Assert.NotSame(legacyProfile, resolved);
        Assert.Equal(23, manager.Config.Monitors[legacyKey].MinBrightness);
        Assert.Same(resolved, manager.Config.Monitors[fallbackKey]);
    }

    [Fact]
    public void GetOrCreateProfile_keeps_profile_continuity_when_display_aliases_are_reordered()
    {
        var manager = CreateManager();
        const string firstIdentity = "edid:DEL4141|INSTANCE-A";
        const string secondIdentity = "edid:DEL4141|INSTANCE-B";

        var first = manager.GetOrCreateProfile(@"\\.\DISPLAY1", firstIdentity);
        first.Multiplier = 0.75;
        var second = manager.GetOrCreateProfile(@"\\.\DISPLAY2", secondIdentity);
        second.Multiplier = 1.25;

        var firstAfterReorder = manager.GetOrCreateProfile(@"\\.\DISPLAY2", firstIdentity);
        var secondAfterReorder = manager.GetOrCreateProfile(@"\\.\DISPLAY1", secondIdentity);

        Assert.Same(first, firstAfterReorder);
        Assert.Equal(0.75, firstAfterReorder.Multiplier);
        Assert.Same(second, secondAfterReorder);
        Assert.Equal(1.25, secondAfterReorder.Multiplier);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private ConfigManager CreateManager()
        => new(ConfigPath, _ => { });
}
