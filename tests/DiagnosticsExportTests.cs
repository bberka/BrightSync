using System.Text;
using System.Text.Json;
using BrightSync.Core.Config;
using BrightSync.Core.Diagnostics;
using BrightSync.Core.Monitors;

namespace BrightSync.Tests;

public sealed class DiagnosticsExportTests
{
    [Fact]
    public void CreateJson_redacts_sensitive_values_and_omits_native_handles_and_raw_capabilities()
    {
        var monitor = new DdcMonitor
        {
            DeviceName = @"\\.\DISPLAY2",
            FriendlyName = @"C:\Users\alice\Display",
            Description = "Monitor profile at C:\\Users\\alice\\display.json",
            DetectionDetails = "authToken=secret-value",
            RawCapabilitiesString = "(prot(monitor)vcp(10 60) access_token=capability-secret)"
        };

        var json = new DiagnosticsExportService().CreateJson(
            new AppConfig { UseLegacyDdcCiDetection = true },
            [monitor],
            ["commandServerAuthToken=command-secret", "Bearer bearer-secret"]);

        Assert.False(json.Contains("secret-value", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("capability-secret", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("command-secret", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("bearer-secret", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("alice", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
        Assert.Contains("[redacted-user-path]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RawCapabilitiesString", json);
        Assert.DoesNotContain("handle", json, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement
            .GetProperty("configuration")
            .GetProperty("useLegacyDdcCiDetection")
            .GetBoolean());
    }

    [Fact]
    public void CreateJson_bounds_monitors_messages_and_encoded_report_size()
    {
        var monitors = Enumerable.Range(0, DiagnosticsExportService.MaxMonitors + 8)
            .Select(index => new DdcMonitor
            {
                DeviceName = $@"\\.\DISPLAY{index}",
                FriendlyName = new string('M', 10_000),
                Description = new string('D', 10_000),
                RawCapabilitiesString = $"(vcp({string.Join(' ', Enumerable.Repeat("10", 200))}))"
            })
            .ToArray();
        var messages = Enumerable.Range(0, DiagnosticsExportService.MaxMessages + 8)
            .Select(index => $"diagnostic-{index}: {new string('x', 2_000)}")
            .ToArray();

        var json = new DiagnosticsExportService().CreateJson(new AppConfig(), monitors, messages);

        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, DiagnosticsExportService.MaxReportBytes);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(monitors.Length, root.GetProperty("monitorCount").GetInt32());
        Assert.True(root.GetProperty("monitorsTruncated").GetBoolean());
        Assert.Equal(DiagnosticsExportService.MaxMonitors, root.GetProperty("monitors").GetArrayLength());
        Assert.Equal(messages.Length, root.GetProperty("diagnostics").GetProperty("messageCount").GetInt32());
        Assert.True(root.GetProperty("diagnostics").GetProperty("messagesTruncated").GetBoolean());
        Assert.Equal(DiagnosticsExportService.MaxMessages,
            root.GetProperty("diagnostics").GetProperty("recentMessages").GetArrayLength());
    }

    [Fact]
    public void CreateJson_includes_representative_monitor_metadata_backend_support_and_capabilities()
    {
        var monitor = new DdcMonitor
        {
            DeviceName = @"\\.\DISPLAY3",
            ManufacturerName = "Acme",
            ModelName = "Vision 27",
            FriendlyName = "Acme Vision 27",
            Description = "Vision 27 firmware",
            ResolutionWidth = 2560,
            ResolutionHeight = 1440,
            RefreshRateHz = 144,
            ConnectionType = "DP",
            IsInternal = false,
            SupportsDdcCi = true,
            SupportsBrightnessRead = true,
            MinNativeBrightness = 5,
            MaxDdcBrightness = 100,
            LastCommandedPercent = 55,
            BrightnessBackend = "Low-level DDC/CI",
            IsHdrSupported = true,
            IsHdrEnabled = true,
            SdrWhiteLevelNits = 203,
            DetectionBackend = "DisplayConfig + WMI",
            DetectionDetails = "Connection from DisplayConfig.",
            SupportsContrast = true,
            SupportsColorPreset = true,
            SupportsInputSource = true,
            SupportedPresets = [5000, 6500],
            SupportedInputs = [15, 16],
            RawCapabilitiesString = "(prot(monitor)type(LCD)vcp(10 12 14 60))"
        };

        var json = new DiagnosticsExportService().CreateJson(new AppConfig(), [monitor]);

        using var document = JsonDocument.Parse(json);
        var exported = document.RootElement.GetProperty("monitors")[0];
        Assert.Equal("Acme", exported.GetProperty("manufacturerName").GetString());
        Assert.Equal("Vision 27", exported.GetProperty("modelName").GetString());
        Assert.Equal(2560, exported.GetProperty("resolutionWidth").GetInt32());
        Assert.Equal(144, exported.GetProperty("refreshRateHz").GetInt32());
        Assert.Equal("DP", exported.GetProperty("connectionType").GetString());
        Assert.True(exported.GetProperty("supportsDdcCi").GetBoolean());
        Assert.True(exported.GetProperty("supportsBrightnessRead").GetBoolean());
        Assert.Equal("Low-level DDC/CI", exported.GetProperty("brightnessBackend").GetString());
        Assert.True(exported.GetProperty("isHdrEnabled").GetBoolean());
        Assert.Equal("DisplayConfig + WMI", exported.GetProperty("detectionBackend").GetString());
        Assert.True(exported.GetProperty("advancedSupport").GetProperty("supportsContrast").GetBoolean());
        Assert.True(exported.GetProperty("capabilities").GetProperty("available").GetBoolean());

        var vcpCodes = exported.GetProperty("capabilities").GetProperty("vcpCodes")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();
        Assert.Contains("0x10", vcpCodes);
        Assert.Contains("0x60", vcpCodes);
    }
}
