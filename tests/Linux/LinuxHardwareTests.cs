using BrightSync.Core.Monitors;
using BrightSync.Platform;
using BrightSync.Platform.Linux.Ddc;
using BrightSync.Platform.Linux.Display;
using BrightSync.Platform.Linux.Monitors;
using BrightSync.Platform.Linux.Services;

namespace BrightSync.Tests.Linux;

public sealed class EdidTests
{
    [Fact]
    public void Parse_ReadsVendorProductSerialAndName()
    {
        var edid = Edid.Parse(EdidFixture.Build("DEL", 0x4141, 0x01020304, "DELL U2720Q", "ABC123"));

        Assert.NotNull(edid);
        Assert.Equal("DEL", edid.ManufacturerCode);
        Assert.Equal((ushort)0x4141, edid.ProductCode);
        Assert.Equal(0x01020304u, edid.SerialNumber);
        Assert.Equal("DELL U2720Q", edid.ModelName);
        Assert.Equal("ABC123", edid.SerialString);
        Assert.Equal("DEL4141", edid.HardwareId);
        Assert.Equal("DEL4141\\ABC123", edid.StableKey);
        Assert.True(edid.ChecksumValid);
        Assert.Equal(60, edid.WidthCm);
    }

    [Fact]
    public void StableKey_FallsBackToNumericSerialThenHardwareId()
    {
        var numeric = Edid.Parse(EdidFixture.Build("SAM", 0x0F7A, 0xDEADBEEF, "Odyssey G4"))!;
        var none = Edid.Parse(EdidFixture.Build("SAM", 0x0F7A, 0, "Odyssey G4"))!;

        Assert.Equal("SAM0F7A\\DEADBEEF", numeric.StableKey);
        Assert.Equal("SAM0F7A", none.StableKey);
    }

    [Fact]
    public void Parse_RejectsBadHeaderAndShortData()
    {
        var data = EdidFixture.Build("DEL", 1, 1, "X");
        data[0] = 0x55;

        Assert.Null(Edid.Parse(data));
        Assert.Null(Edid.Parse(new byte[64]));
    }

    [Fact]
    public void Parse_FlagsBadChecksum()
    {
        var data = EdidFixture.Build("DEL", 1, 1, "X");
        data[127] ^= 0xFF;

        Assert.False(Edid.Parse(data)!.ChecksumValid);
    }
}

public sealed class DrmScannerTests
{
    private const string Root = "/sys/class/drm";

    [Fact]
    public void Scan_ReturnsOnlyConnectedConnectorsWithBusEdidAndMode()
    {
        var sysfs = new FakeSysfs()
            .AddFile($"{Root}/card1-DP-1/status", "connected\n")
            .AddFile($"{Root}/card1-DP-1/edid", EdidFixture.Build("GSM", 0x5B7E, 7, "LG ULTRAGEAR"))
            .AddFile($"{Root}/card1-DP-1/modes", "2560x1440\n1920x1080\n")
            .AddLink($"{Root}/card1-DP-1/ddc", "i2c-7")
            .AddFile($"{Root}/card1-HDMI-A-1/status", "disconnected\n")
            .AddFile($"{Root}/card1-eDP-1/status", "connected\n")
            .AddFile($"{Root}/card1-eDP-1/modes", "1920x1200\n")
            .AddDirectory($"{Root}/card1")
            .AddDirectory($"{Root}/version");

        var connectors = DrmScanner.Scan(sysfs);

        Assert.Equal(2, connectors.Count);
        var external = connectors.Single(c => c.Name == "DP-1");
        Assert.Equal("DP", external.ConnectionType);
        Assert.False(external.IsInternal);
        Assert.Equal(7, external.I2cBus);
        Assert.Equal(2560, external.Width);
        Assert.Equal(1440, external.Height);
        Assert.Equal("LG ULTRAGEAR", external.Edid!.ModelName);

        var panel = connectors.Single(c => c.Name == "eDP-1");
        Assert.True(panel.IsInternal);
        Assert.Equal("eDP", panel.ConnectionType);
        Assert.Null(panel.I2cBus);
        Assert.Null(panel.Edid);
    }

    [Fact]
    public void Scan_FindsI2cBusFromChildDirectoryWhenDdcLinkIsMissing()
    {
        var sysfs = new FakeSysfs()
            .AddFile($"{Root}/card0-HDMI-A-2/status", "connected\n")
            .AddDirectory($"{Root}/card0-HDMI-A-2/i2c-12");

        var connector = Assert.Single(DrmScanner.Scan(sysfs));

        Assert.Equal(12, connector.I2cBus);
        Assert.Equal("HDMI", connector.ConnectionType);
    }

    [Theory]
    [InlineData("HDMI-A-1", "HDMI", false)]
    [InlineData("DP-3", "DP", false)]
    [InlineData("eDP-1", "eDP", true)]
    [InlineData("LVDS-1", "LVDS", true)]
    [InlineData("DSI-1", "DSI", true)]
    [InlineData("DVI-D-1", "DVI", false)]
    [InlineData("VGA-1", "VGA", false)]
    public void ConnectorNames_MapToTypeAndInternalFlag(string name, string type, bool internalPanel)
    {
        Assert.Equal(type, DrmScanner.MapConnectionType(name));
        Assert.Equal(internalPanel, DrmScanner.IsInternalConnector(name));
    }

    [Fact]
    public void DisplaySignature_ChangesWhenADisplayAppears()
    {
        var before = new FakeSysfs().AddFile($"{Root}/card1-DP-1/status", "connected\n");
        var after = new FakeSysfs()
            .AddFile($"{Root}/card1-DP-1/status", "connected\n")
            .AddFile($"{Root}/card1-HDMI-A-1/status", "connected\n");

        Assert.NotEqual(
            LinuxSystemEvents.ComputeDisplaySignature(DrmScanner.Scan(before)),
            LinuxSystemEvents.ComputeDisplaySignature(DrmScanner.Scan(after)));
    }
}

public sealed class DdcCiProtocolTests
{
    private static DdcCiProtocol Protocol(FakeDisplayBus bus) => new(bus, _ => { });

    [Fact]
    public void BuildFrame_MatchesDdcSpecificationExample()
    {
        // Set VCP 0x10 = 0x32: 51 84 03 10 00 32 then checksum with the 0x6E destination seed.
        var frame = DdcCiProtocol.BuildFrame([0x03, 0x10, 0x00, 0x32]);

        Assert.Equal(new byte[] { 0x51, 0x84, 0x03, 0x10, 0x00, 0x32, 0x6E ^ 0x51 ^ 0x84 ^ 0x03 ^ 0x10 ^ 0x00 ^ 0x32 }, frame);
    }

    [Fact]
    public void TryGetVcp_ParsesCurrentAndMaximum()
    {
        var bus = new FakeDisplayBus().WithFeature(0x10, 40, 100);

        Assert.True(Protocol(bus).TryGetVcp(0x10, out var current, out var max));
        Assert.Equal(40u, current);
        Assert.Equal(100u, max);
    }

    [Fact]
    public void TryGetVcp_FailsForUnsupportedFeatureAndSilentDisplay()
    {
        Assert.False(Protocol(new FakeDisplayBus()).TryGetVcp(0x12, out _, out _));
        Assert.False(Protocol(new FakeDisplayBus { Silent = true }.WithFeature(0x10, 1, 100)).TryGetVcp(0x10, out _, out _));
    }

    [Fact]
    public void TryGetVcp_RejectsCorruptChecksum()
    {
        var reply = new byte[] { 0x6E, 0x88, 0x02, 0x00, 0x10, 0x00, 0x00, 0x64, 0x00, 0x28, 0x00 };

        Assert.False(DdcCiProtocol.ValidateReply(reply, out _));
    }

    [Fact]
    public void TrySetVcp_SendsValueBigEndian()
    {
        var bus = new FakeDisplayBus();

        Assert.True(Protocol(bus).TrySetVcp(0x10, 0x0123));

        Assert.Equal((0x10, 0x0123u), Assert.Single(bus.Writes));
    }

    [Fact]
    public void TrySetVcp_ReportsWriteFailure()
    {
        Assert.False(Protocol(new FakeDisplayBus { FailWrites = true }).TrySetVcp(0x10, 5));
    }

    [Fact]
    public void ReadCapabilities_ReassemblesFragments()
    {
        var text = "(prot(monitor)type(lcd)model(U2720Q)cmds(01 02 03 07 0C)vcp(02 04 05 08 10 12 14(01 05 08 0B) 16 18 1A 60(0F 10 11) 62 AC AE B2 B6)mswhql(1))";
        var bus = new FakeDisplayBus(text);

        Assert.Equal(text, Protocol(bus).ReadCapabilities());
    }
}

public sealed class BacklightBrightnessTests
{
    private const string Root = "/sys/class/backlight";

    private static FakeSysfs Panel(int current = 500, int max = 1000, string type = "firmware", string name = "intel_backlight")
        => new FakeSysfs()
            .AddFile($"{Root}/{name}/brightness", current.ToString())
            .AddFile($"{Root}/{name}/actual_brightness", current.ToString())
            .AddFile($"{Root}/{name}/max_brightness", max.ToString())
            .AddFile($"{Root}/{name}/type", type);

    [Fact]
    public void Read_ReturnsPercent()
    {
        var backlight = new BacklightBrightness(Panel(250, 1000), (_, _) => false);

        Assert.Equal(25, backlight.ReadCurrentBrightness());
    }

    [Fact]
    public void Read_ReturnsMinusOneWithoutDevice()
    {
        var backlight = new BacklightBrightness(new FakeSysfs(), (_, _) => false);

        Assert.Equal(-1, backlight.ReadCurrentBrightness());
        Assert.False(backlight.TrySetBrightness(50));
    }

    [Fact]
    public void Set_WritesRawValueToSysfs()
    {
        var written = new List<(string Path, string Value)>();
        var backlight = new BacklightBrightness(Panel(), (_, _) => false, (p, v) =>
        {
            written.Add((p, v));
            return true;
        });

        Assert.True(backlight.TrySetBrightness(30));

        Assert.Equal(($"{Root}/intel_backlight/brightness", "300"), Assert.Single(written));
    }

    [Fact]
    public void Set_FallsBackToLogindWhenSysfsIsDenied()
    {
        (string Name, uint Raw)? logind = null;
        var backlight = new BacklightBrightness(Panel(), (name, raw) =>
        {
            logind = (name, raw);
            return true;
        }, (_, _) => false);

        Assert.True(backlight.TrySetBrightness(80));

        Assert.Equal(("intel_backlight", 800u), logind);
    }

    [Fact]
    public void Set_NeverWritesRawZeroForNonZeroRequest()
    {
        Assert.Equal(1, BacklightBrightness.ToRaw(0, 1000));
        Assert.Equal(1, BacklightBrightness.ToRaw(1, 20));
        Assert.Equal(1000, BacklightBrightness.ToRaw(100, 1000));
    }

    [Fact]
    public void FindDevice_PrefersFirmwareOverRaw()
    {
        var sysfs = Panel(100, 255, "raw", "nvidia_0")
            .AddFile($"{Root}/acpi_video0/brightness", "50")
            .AddFile($"{Root}/acpi_video0/max_brightness", "100")
            .AddFile($"{Root}/acpi_video0/type", "firmware");
        string? target = null;
        var backlight = new BacklightBrightness(sysfs, (_, _) => false, (path, _) =>
        {
            target = path;
            return true;
        });

        backlight.TrySetBrightness(10);

        Assert.Equal($"{Root}/acpi_video0/brightness", target);
    }
}

public sealed class LinuxMonitorBackendTests
{
    private const string Drm = "/sys/class/drm";

    private static FakeSysfs ExternalDisplay()
        => new FakeSysfs()
            .AddFile($"{Drm}/card1-DP-1/status", "connected\n")
            .AddFile($"{Drm}/card1-DP-1/edid", EdidFixture.Build("DEL", 0x4141, 9, "DELL U2720Q", "SN001"))
            .AddFile($"{Drm}/card1-DP-1/modes", "2560x1440\n")
            .AddLink($"{Drm}/card1-DP-1/ddc", "i2c-5");

    private static LinuxMonitorBackend Backend(FakeSysfs sysfs, FakeDisplayBus? bus, IInternalBrightness? panel = null)
        => new(sysfs, panel ?? new FakePanel(), _ => bus);

    [Fact]
    public void Enumerate_BuildsControllableExternalMonitorWithAdvancedControls()
    {
        var bus = new FakeDisplayBus()
            .WithFeature(VcpCodes.Brightness, 50, 100)
            .WithFeature(VcpCodes.Contrast, 70, 100);
        var backend = Backend(ExternalDisplay(), bus);

        var set = backend.Enumerate(false, CancellationToken.None);
        var monitor = Assert.Single(set.Monitors);

        Assert.Equal("Dell U2720Q", monitor.FriendlyName);
        Assert.Equal("DP-1", monitor.DeviceName);
        Assert.True(monitor.SupportsDdcCi);
        Assert.True(monitor.SupportsBrightnessRead);
        Assert.Equal(MonitorBrightnessBackend.LowLevelDdcCi, monitor.BrightnessBackendType);
        Assert.Equal(50, monitor.LastCommandedPercent);
        Assert.True(monitor.SupportsContrast);
        Assert.Equal(70, monitor.CurrentContrast);
        Assert.StartsWith("edid:", monitor.StableIdentity);
        Assert.Equal(2560, monitor.ResolutionWidth);

        set.DisposeResources();
        Assert.True(bus.Disposed);
    }

    [Fact]
    public void SetAndGetBrightness_RoundTripThroughTheBus()
    {
        var bus = new FakeDisplayBus().WithFeature(VcpCodes.Brightness, 10, 100);
        var backend = Backend(ExternalDisplay(), bus);
        var monitor = backend.Enumerate(false, CancellationToken.None).Monitors.Single();

        Assert.True(backend.SetBrightness(monitor, 65));
        Assert.True(backend.TryGetBrightness(monitor, out var percent));

        Assert.Equal(65, percent);
        Assert.Contains((VcpCodes.Brightness, 65u), bus.Writes);
    }

    [Fact]
    public void Enumerate_MarksMonitorUnsupportedWhenBusCannotBeOpened()
    {
        var backend = Backend(ExternalDisplay(), bus: null);

        var monitor = backend.Enumerate(false, CancellationToken.None).Monitors.Single();

        Assert.False(monitor.SupportsDdcCi);
        Assert.Contains("/dev/i2c-5", monitor.DetectionDetails);
    }

    [Fact]
    public void Enumerate_UsesWriteOnlyBackendWhenCapabilitiesAdvertiseBrightness()
    {
        var bus = new FakeDisplayBus("(prot(monitor)vcp(10 12))");
        var backend = Backend(ExternalDisplay(), bus);

        var monitor = backend.Enumerate(false, CancellationToken.None).Monitors.Single();

        Assert.Equal(MonitorBrightnessBackend.WriteOnlyDdcCi, monitor.BrightnessBackendType);
        Assert.True(monitor.SupportsDdcCi);
        Assert.False(monitor.SupportsBrightnessRead);
    }

    [Fact]
    public void Enumerate_RoutesInternalPanelThroughBacklight()
    {
        var sysfs = new FakeSysfs()
            .AddFile($"{Drm}/card0-eDP-1/status", "connected\n")
            .AddFile($"{Drm}/card0-eDP-1/modes", "1920x1080\n");
        var panel = new FakePanel { Brightness = 42 };
        var backend = Backend(sysfs, null, panel);

        var monitor = backend.Enumerate(false, CancellationToken.None).Monitors.Single();

        Assert.True(monitor.IsInternal);
        Assert.Equal(MonitorBrightnessBackend.InternalPanel, monitor.BrightnessBackendType);
        Assert.Equal(42, monitor.LastCommandedPercent);

        Assert.True(backend.SetBrightness(monitor, 77));
        Assert.Equal(77, panel.Brightness);
        Assert.False(backend.SetVcpFeature(monitor, VcpCodes.Contrast, 1));
    }

    [Fact]
    public void Enumerate_GivesDuplicateEdidsDistinctIdentities()
    {
        var sysfs = ExternalDisplay()
            .AddFile($"{Drm}/card1-DP-2/status", "connected\n")
            .AddFile($"{Drm}/card1-DP-2/edid", EdidFixture.Build("DEL", 0x4141, 9, "DELL U2720Q", "SN001"))
            .AddLink($"{Drm}/card1-DP-2/ddc", "i2c-6");

        var monitors = Backend(sysfs, new FakeDisplayBus().WithFeature(VcpCodes.Brightness, 1, 100))
            .Enumerate(false, CancellationToken.None).Monitors;

        Assert.Equal(2, monitors.Select(m => m.StableIdentity).Distinct().Count());
    }

    private sealed class FakePanel : IInternalBrightness
    {
        public int Brightness { get; set; } = 50;
        public int ReadCurrentBrightness() => Brightness;

        public bool TrySetBrightness(int brightnessPercent)
        {
            Brightness = brightnessPercent;
            return true;
        }
    }
}

public sealed class LinuxServiceTests
{
    [Fact]
    public void AutostartEntry_QuotesExecutableAndPassesAutostartFlag()
    {
        var entry = LinuxAutoStartManager.BuildDesktopEntry("/opt/Bright Sync/$bin/BrightSync");

        Assert.Contains("Exec=\"/opt/Bright Sync/\\$bin/BrightSync\" --autostart\n", entry);
        Assert.StartsWith("[Desktop Entry]\n", entry);
        Assert.Contains("X-GNOME-Autostart-enabled=true", entry);
    }

    [Fact]
    public void Autostart_WritesAndRemovesDesktopFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "brightsync-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new LinuxAutoStartManager(directory, "/usr/bin/brightsync");

            manager.Apply(true);
            var path = Path.Combine(directory, "brightsync.desktop");
            Assert.Contains("Exec=\"/usr/bin/brightsync\" --autostart", File.ReadAllText(path));

            manager.Apply(false);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SingleInstanceGuard_AllowsOnlyOneHolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "brightsync-test-" + Guid.NewGuid().ToString("N") + ".lock");
        try
        {
            using var first = new LinuxSingleInstanceGuard(path);
            using var second = new LinuxSingleInstanceGuard(path);

            Assert.True(first.TryAcquire());
            Assert.False(second.TryAcquire());

            first.Dispose();
            using var third = new LinuxSingleInstanceGuard(path);
            Assert.True(third.TryAcquire());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("power-saver", true)]
    [InlineData("balanced", false)]
    [InlineData("performance", false)]
    [InlineData(null, false)]
    public void EnergySaver_ActiveOnlyForPowerSaverProfile(string? profile, bool expected)
    {
        using var source = new LinuxEnergySaverSource(() => profile);
        bool? raised = null;
        source.Changed += (_, active) => raised = active;

        source.Poll();

        Assert.Equal(expected, source.IsActive);
        Assert.Equal(expected ? true : null, raised);
    }

    [Fact]
    public void EnergySaver_RaisesOnlyOnTransitions()
    {
        var profile = "balanced";
        using var source = new LinuxEnergySaverSource(() => profile);
        var raised = new List<bool>();
        source.Changed += (_, active) => raised.Add(active);

        source.Poll();
        profile = "power-saver";
        source.Poll();
        source.Poll();
        profile = "balanced";
        source.Poll();

        Assert.Equal(new[] { true, false }, raised);
    }

    [Fact]
    public void ClockJumpDetector_IgnoresNormalDriftAndFlagsJumps()
    {
        var detector = new ClockJumpDetector();
        var start = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(detector.Check(start, 0));
        Assert.False(detector.Check(start.AddSeconds(5.2), 5_000));
        Assert.True(detector.Check(start.AddHours(1), 10_000));
        Assert.False(detector.Check(start.AddHours(1).AddSeconds(5), 15_000));
    }
}

public sealed class LinuxDesktopFeatureTests
{
    [Fact]
    public void Detect_ReportsFullGnomeSession()
    {
        var features = BrightSync.Platform.Linux.LinuxDesktopFeatures.Detect(
            ["org.freedesktop.login1", "net.hadess.PowerProfiles"],
            ["org.gnome.Mutter.IdleMonitor", "org.gnome.ScreenSaver", "org.mpris.MediaPlayer2.vlc"],
            x11IdleAvailable: false);

        Assert.True(features.DetectsEnergySaver);
        Assert.True(features.DetectsMediaPlayback);
        Assert.True(features.DetectsIdleTime);
        Assert.True(features.DetectsSessionLock);
    }

    [Fact]
    public void Detect_HidesEverythingWithoutBuses()
    {
        var features = BrightSync.Platform.Linux.LinuxDesktopFeatures.Detect([], [], x11IdleAvailable: false);

        Assert.False(features.DetectsEnergySaver);
        Assert.False(features.DetectsMediaPlayback);
        Assert.False(features.DetectsIdleTime);
        Assert.False(features.DetectsSessionLock);
    }

    [Fact]
    public void Detect_UsesX11FallbackForIdleAndLogindForLock()
    {
        var features = BrightSync.Platform.Linux.LinuxDesktopFeatures.Detect(
            ["org.freedesktop.login1"], ["org.freedesktop.DBus"], x11IdleAvailable: true);

        Assert.True(features.DetectsIdleTime);
        Assert.True(features.DetectsSessionLock);
        Assert.False(features.DetectsEnergySaver);
    }
}

public sealed class InertEnergySaverTests
{
    [Fact]
    public void Inert_NeverReportsActiveOrRaises()
    {
        using var source = new InertEnergySaverSource();
        var raised = false;
        source.Changed += (_, _) => raised = true;

        source.Start();

        Assert.False(source.IsActive);
        Assert.False(raised);
    }
}
