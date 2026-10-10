using BrightSync.Core.Monitors;
using BrightSync.Platform.Linux.Ddc;
using BrightSync.Platform.Linux.Display;
using Serilog;

namespace BrightSync.Platform.Linux.Monitors;

/// <summary>
/// Linux monitor backend. Displays come from the kernel DRM connectors in sysfs; external panels are
/// driven with DDC/CI over <c>/dev/i2c-N</c>, built-in panels through the backlight class.
/// </summary>
internal sealed class LinuxMonitorBackend(
    ISysfs sysfs,
    IInternalBrightness internalBrightness,
    Func<int, II2cBus?> openBus) : IMonitorBackend
{
    public void InvalidateCaches()
    {
    }

    public DdcMonitorSet Enumerate(bool useLegacyDetection, CancellationToken cancellationToken)
    {
        var monitors = new List<DdcMonitor>();
        var resources = new List<IDisposable>();

        try
        {
            foreach (var connector in DrmScanner.Scan(sysfs))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var monitor = connector.IsInternal
                    ? BuildInternal(connector)
                    : BuildExternal(connector, resources, cancellationToken);
                monitors.Add(monitor);
            }

            MonitorIdentityResolver.EnsureUniqueIdentities(monitors);
            return new DdcMonitorSet(monitors, resources);
        }
        catch
        {
            foreach (var resource in resources)
                resource.Dispose();
            throw;
        }
    }

    public bool SetBrightness(DdcMonitor monitor, int brightnessPercent)
    {
        switch (monitor.BrightnessBackendType)
        {
            case MonitorBrightnessBackend.InternalPanel:
                return internalBrightness.TrySetBrightness(brightnessPercent);
            case MonitorBrightnessBackend.LowLevelDdcCi:
            case MonitorBrightnessBackend.WriteOnlyDdcCi:
                if (monitor.BackendState is not DdcCiProtocol protocol)
                    return false;

                var value = (uint)Math.Round(brightnessPercent / 100.0 * monitor.MaxDdcBrightness);
                return Retry(() => protocol.TrySetVcp(VcpCodes.Brightness, value));
            default:
                return false;
        }
    }

    public bool TryGetBrightness(DdcMonitor monitor, out int brightnessPercent)
    {
        brightnessPercent = 0;
        switch (monitor.BrightnessBackendType)
        {
            case MonitorBrightnessBackend.InternalPanel:
                brightnessPercent = internalBrightness.ReadCurrentBrightness();
                return brightnessPercent >= 0;
            case MonitorBrightnessBackend.LowLevelDdcCi:
                if (monitor.BackendState is not DdcCiProtocol protocol)
                    return false;

                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (protocol.TryGetVcp(VcpCodes.Brightness, out var current, out var max) && max > 0)
                    {
                        brightnessPercent = (int)Math.Round(current * 100.0 / max);
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    public bool SetVcpFeature(DdcMonitor monitor, byte vcpCode, uint value)
        => monitor.BackendState is DdcCiProtocol protocol && Retry(() => protocol.TrySetVcp(vcpCode, value));

    public bool GetVcpFeature(DdcMonitor monitor, byte vcpCode, out uint currentValue, out uint maxValue)
    {
        currentValue = 0;
        maxValue = 0;
        if (monitor.BackendState is not DdcCiProtocol protocol)
            return false;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (protocol.TryGetVcp(vcpCode, out currentValue, out maxValue))
                return true;
        }

        return false;
    }

    private DdcMonitor BuildInternal(DrmConnector connector)
    {
        var identity = IdentityOf(connector);
        var current = internalBrightness.ReadCurrentBrightness();
        var controllable = current >= 0;
        return new DdcMonitor
        {
            DeviceName = connector.Name,
            StableIdentity = StableIdentityOf(connector),
            ManufacturerName = identity.ManufacturerName,
            ModelName = identity.ModelName,
            FriendlyName = identity.FriendlyName,
            Description = "Internal Display",
            ResolutionWidth = connector.Width,
            ResolutionHeight = connector.Height,
            ConnectionType = connector.ConnectionType,
            IsInternal = true,
            SupportsDdcCi = controllable,
            SupportsBrightnessRead = controllable,
            MinNativeBrightness = 0,
            MaxDdcBrightness = 100,
            LastCommandedPercent = controllable ? current : 50,
            BrightnessBackendType = controllable ? MonitorBrightnessBackend.InternalPanel : MonitorBrightnessBackend.None,
            BrightnessBackend = controllable ? "Backlight (Internal)" : "Unavailable",
            DetectionBackend = "DRM + Backlight",
            DetectionDetails = controllable
                ? "Internal display controlled through /sys/class/backlight."
                : "No usable /sys/class/backlight device was found for the internal display."
        };
    }

    private DdcMonitor BuildExternal(DrmConnector connector, List<IDisposable> resources, CancellationToken cancellationToken)
    {
        var identity = IdentityOf(connector);
        var details = new List<string>();
        DdcCiProtocol? protocol = null;
        var backend = MonitorBrightnessBackend.None;
        var backendLabel = "Unavailable";
        var supportsRead = false;
        var max = 100u;
        var currentPercent = -1;
        string? capabilities = null;

        if (connector.I2cBus is not { } busNumber)
        {
            details.Add("The kernel did not expose an I2C bus for this connector.");
        }
        else
        {
            var bus = openBus(busNumber);
            if (bus is null)
            {
                details.Add(
                    $"/dev/i2c-{busNumber} could not be opened. Load the i2c-dev module and grant access (see docs/LINUX.md).");
            }
            else
            {
                resources.Add(bus);
                protocol = new DdcCiProtocol(bus);
                cancellationToken.ThrowIfCancellationRequested();

                if (TryReadBrightness(protocol, out var current, out var maximum))
                {
                    backend = MonitorBrightnessBackend.LowLevelDdcCi;
                    backendLabel = "DDC/CI";
                    supportsRead = true;
                    max = maximum;
                    currentPercent = (int)Math.Round(current * 100.0 / maximum);
                    details.Add($"Brightness control is available through DDC/CI VCP code 0x{VcpCodes.Brightness:X2}.");
                }
                else
                {
                    capabilities = protocol.ReadCapabilities();
                    if (capabilities is not null && VcpCapabilities.IndicatesBrightnessSupport(capabilities))
                    {
                        backend = MonitorBrightnessBackend.WriteOnlyDdcCi;
                        backendLabel = "DDC/CI write-only";
                        details.Add(
                            "The monitor capabilities string advertised brightness support even though a direct brightness read did not succeed.");
                    }
                    else
                    {
                        details.Add("The monitor did not answer DDC/CI brightness requests (DDC/CI may be disabled in its OSD).");
                    }
                }
            }
        }

        var supported = backend != MonitorBrightnessBackend.None;
        var monitor = new DdcMonitor
        {
            DeviceName = connector.Name,
            StableIdentity = StableIdentityOf(connector),
            ManufacturerName = identity.ManufacturerName,
            ModelName = identity.ModelName,
            FriendlyName = identity.FriendlyName,
            Description = identity.FriendlyName,
            ResolutionWidth = connector.Width,
            ResolutionHeight = connector.Height,
            ConnectionType = connector.ConnectionType,
            IsInternal = false,
            SupportsDdcCi = supported,
            SupportsBrightnessRead = supportsRead,
            MinNativeBrightness = 0,
            MaxDdcBrightness = (int)max,
            LastCommandedPercent = currentPercent,
            BrightnessBackendType = backend,
            BrightnessBackend = backendLabel,
            IsAppleDisplay = string.Equals(identity.ManufacturerName, "Apple", StringComparison.OrdinalIgnoreCase),
            IsAppleStudioDisplay = identity.FriendlyName.Contains("Studio Display", StringComparison.OrdinalIgnoreCase),
            DetectionBackend = $"DRM + {backendLabel}",
            DetectionDetails = string.Join(" ", details),
            BackendState = protocol
        };

        if (supported && protocol is not null)
        {
            VcpCapabilities.ProbeAdvancedControls(
                monitor,
                capabilities ?? protocol.ReadCapabilities(),
                (byte code, out uint value, out uint maximum) => TryReadWithRetry(protocol, code, out value, out maximum),
                cancellationToken);
        }

        Log.Debug(
            "Detected monitor. Connector={Connector}, FriendlyName={FriendlyName}, SupportsDdcCi={SupportsDdcCi}, Backend={Backend}",
            connector.SysfsName,
            identity.FriendlyName,
            supported,
            backendLabel);
        return monitor;
    }

    private static bool TryReadBrightness(DdcCiProtocol protocol, out uint current, out uint maximum)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (protocol.TryGetVcp(VcpCodes.Brightness, out current, out maximum) && maximum > 0)
                return true;
        }

        current = 0;
        maximum = 0;
        return false;
    }

    private static bool TryReadWithRetry(DdcCiProtocol protocol, byte code, out uint value, out uint maximum)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (protocol.TryGetVcp(code, out value, out maximum))
                return true;
        }

        value = 0;
        maximum = 0;
        return false;
    }

    private static bool Retry(Func<bool> action)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (action())
                return true;
        }

        return false;
    }

    private static MonitorIdentity IdentityOf(DrmConnector connector)
    {
        var edid = connector.Edid;
        if (edid is null)
            return MonitorNames.BuildIdentity(string.Empty, connector.Name);

        var manufacturer = MonitorNames.DecodeManufacturerCode(edid.ManufacturerCode);
        var model = string.IsNullOrWhiteSpace(edid.ModelName) ? $"{edid.ProductCode:X4}" : edid.ModelName;
        return MonitorNames.BuildIdentity(manufacturer, model);
    }

    private static string StableIdentityOf(DrmConnector connector)
        => connector.Edid is { } edid
            ? MonitorIdentityResolver.Resolve($"EDID\\{edid.StableKey}", null, connector.Name)
            : MonitorIdentityResolver.Resolve(null, null, connector.Name);
}
