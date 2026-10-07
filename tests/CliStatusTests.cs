using System.Text;
using System.Text.Json;
using BrightSync.Cli;
using BrightSync.Core.Brightness;
using BrightSync.Core.Config;

namespace BrightSync.Tests;

public sealed class CliStatusTests
{
    [Theory]
    [InlineData(16384, true)]
    [InlineData(16385, false)]
    public async Task Resident_protocol_body_reader_enforces_request_limit(int byteCount, bool expectedRead)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', byteCount)));

        var body = await ResidentCommandServer.ReadBoundedBodyAsync(stream, CancellationToken.None);

        if (expectedRead)
            Assert.Equal(byteCount, body!.Length);
        else
            Assert.Null(body);
    }

    [Fact]
    public void SnapshotFactory_includes_state_counts_and_remaining_expiry()
    {
        var timestamp = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var eyeProtectionEnd = timestamp.UtcDateTime.AddSeconds(90);
        var snapshot = CliStatusSnapshotFactory.Create(
            masterBrightness: 61,
            automaticBrightnessEnabled: true,
            eyeProtectionActive: true,
            eyeProtectionEndUtc: eyeProtectionEnd,
            brightnessBoostActive: false,
            brightnessBoostEndUtc: null,
            monitorCount: 3,
            controllableMonitorCount: 2,
            timestampUtc: timestamp,
            version: "0.17");

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal("0.17", snapshot.Version);
        Assert.Equal(timestamp, snapshot.TimestampUtc);
        Assert.Equal(61, snapshot.MasterBrightness);
        Assert.True(snapshot.AutomaticBrightnessEnabled);
        Assert.True(snapshot.EyeProtectionActive);
        Assert.Equal(new DateTimeOffset(eyeProtectionEnd), snapshot.EyeProtectionExpiresUtc);
        Assert.Equal(90, snapshot.EyeProtectionRemainingSeconds);
        Assert.False(snapshot.BrightnessBoostActive);
        Assert.Null(snapshot.BrightnessBoostExpiresUtc);
        Assert.Null(snapshot.BrightnessBoostRemainingSeconds);
        Assert.Equal(3, snapshot.MonitorCount);
        Assert.Equal(2, snapshot.ControllableMonitorCount);
    }

    [Fact]
    public void SnapshotFactory_reports_zero_remaining_for_expired_mode_and_null_for_unknown_expiry()
    {
        var timestamp = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var snapshot = CliStatusSnapshotFactory.Create(
            masterBrightness: 40,
            automaticBrightnessEnabled: false,
            eyeProtectionActive: true,
            eyeProtectionEndUtc: timestamp.UtcDateTime.AddSeconds(-1),
            brightnessBoostActive: true,
            brightnessBoostEndUtc: null,
            monitorCount: 1,
            controllableMonitorCount: 0,
            timestampUtc: timestamp,
            version: "test");

        Assert.Equal(0, snapshot.EyeProtectionRemainingSeconds);
        Assert.Equal(new DateTimeOffset(timestamp.UtcDateTime.AddSeconds(-1)), snapshot.EyeProtectionExpiresUtc);
        Assert.True(snapshot.BrightnessBoostActive);
        Assert.Null(snapshot.BrightnessBoostExpiresUtc);
        Assert.Null(snapshot.BrightnessBoostRemainingSeconds);
    }

    [Fact]
    public async Task Handler_returns_status_snapshot_without_mutating_resident_state()
    {
        using var context = new TestContext();
        var config = context.CreateConfig();
        var engine = new FakeBrightnessEngine();
        using var autoBrightness = new AutoBrightnessService(engine, config);
        using var eyeProtection = new EyeProtectionService(engine, config);
        using var brightnessBoost = new BrightnessBoostService(engine, config);
        var snapshot = new CliStatusSnapshot
        {
            SchemaVersion = 1,
            Version = "test",
            TimestampUtc = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero),
            MasterBrightness = 55,
            AutomaticBrightnessEnabled = false,
            EyeProtectionActive = true,
            EyeProtectionExpiresUtc = new DateTimeOffset(2026, 10, 7, 13, 0, 0, TimeSpan.Zero),
            EyeProtectionRemainingSeconds = 3600,
            BrightnessBoostActive = false,
            MonitorCount = 2,
            ControllableMonitorCount = 1
        };
        var handler = new ResidentCommandHandler(
            engine,
            autoBrightness,
            eyeProtection,
            brightnessBoost,
            () => { },
            () => { },
            () => { },
            static (callback, _) => Task.FromResult(callback()),
            () => snapshot);

        var response = await handler.HandleAsync(
            new CommandRequest { CommandType = AppCommandType.Status, JsonOutput = true },
            CancellationToken.None);

        Assert.True(response.Success);
        Assert.Same(snapshot, response.Status);
        Assert.Equal(CliExitCode.Success, response.ExitCode);
        Assert.Contains("Master brightness: 55%", response.Message);
        Assert.Contains("Automatic brightness: disabled", response.Message);
        Assert.Contains("Monitors: 2 total, 1 controllable", response.Message);
        Assert.Equal(0, engine.UserBrightnessChanges);
        Assert.False(File.Exists(context.ConfigPath));

        var result = response.ToExecutionResult(jsonOutput: true);
        using var json = JsonDocument.Parse(result.Message);
        Assert.Equal(55, json.RootElement.GetProperty("masterBrightness").GetInt32());
        Assert.Equal(3600, json.RootElement.GetProperty("eyeProtectionRemainingSeconds").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("controllableMonitorCount").GetInt32());
        Assert.DoesNotContain("BearerToken", result.Message);
        Assert.DoesNotContain("config.json", result.Message);
    }

    private sealed class FakeBrightnessEngine : IBrightnessEngineOperations
    {
        public event EventHandler<int>? MasterBrightnessChanged;
        public event EventHandler? TargetsChanged;

        public int MasterBrightness { get; private set; } = 55;
        public bool IsIdleReductionActive => false;
        public int UserBrightnessChanges { get; private set; }

        public bool ApplyAutomaticBrightness(int brightness)
        {
            MasterBrightness = brightness;
            MasterBrightnessChanged?.Invoke(this, brightness);
            return true;
        }

        public bool TrySetUserBrightness(int brightness)
        {
            UserBrightnessChanges++;
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
