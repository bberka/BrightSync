using BrightSync.Core;
using BrightSync.Core.Brightness;
using Serilog;

namespace BrightSync.Cli;

public sealed class ResidentCommandHandler
{
    private readonly IBrightnessEngineOperations _engine;
    private readonly AutoBrightnessService _autoBrightnessService;
    private readonly EyeProtectionService _eyeProtectionService;
    private readonly BrightnessBoostService _brightnessBoostService;
    private readonly Action _showSettings;
    private readonly Action _refreshMonitors;
    private readonly Action _requestAppExit;
    private readonly Func<Func<CommandResponse>, CancellationToken, Task<CommandResponse>> _invokeOnUiThread;
    private readonly Func<CliStatusSnapshot> _getStatusSnapshot;

    public ResidentCommandHandler(
        BrightSyncEngine engine,
        AutoBrightnessService autoBrightnessService,
        EyeProtectionService eyeProtectionService,
        BrightnessBoostService brightnessBoostService,
        IResidentAppHost appHost,
        Action requestAppExit)
        : this(
            new BrightnessEngineOperations(engine),
            autoBrightnessService,
            eyeProtectionService,
            brightnessBoostService,
            appHost.ShowSettings,
            appHost.RefreshMonitorsFromCommand,
            requestAppExit,
            static (handler, cancellationToken) => UiDispatcher.Current.InvokeAsync(handler, cancellationToken),
            () => CliStatusSnapshotFactory.Create(
                engine,
                autoBrightnessService,
                eyeProtectionService,
                brightnessBoostService))
    {
    }

    internal ResidentCommandHandler(
        IBrightnessEngineOperations engine,
        AutoBrightnessService autoBrightnessService,
        EyeProtectionService eyeProtectionService,
        BrightnessBoostService brightnessBoostService,
        Action showSettings,
        Action refreshMonitors,
        Action requestAppExit,
        Func<Func<CommandResponse>, CancellationToken, Task<CommandResponse>> invokeOnUiThread,
        Func<CliStatusSnapshot>? getStatusSnapshot = null)
    {
        _engine = engine;
        _autoBrightnessService = autoBrightnessService;
        _eyeProtectionService = eyeProtectionService;
        _brightnessBoostService = brightnessBoostService;
        _showSettings = showSettings;
        _refreshMonitors = refreshMonitors;
        _requestAppExit = requestAppExit;
        _invokeOnUiThread = invokeOnUiThread;
        _getStatusSnapshot = getStatusSnapshot ?? CreateFallbackStatusSnapshot;
    }

    public async Task<CommandResponse> HandleAsync(CommandRequest request, CancellationToken cancellationToken)
        => await _invokeOnUiThread(() => HandleCore(request), cancellationToken);

    private CommandResponse HandleCore(CommandRequest request)
    {
        try
        {
            return request.CommandType switch
            {
                AppCommandType.BrightnessSet => SetBrightness(request.BrightnessValue),
                AppCommandType.BrightnessUp => StepBrightness(request.StepValue, direction: 1),
                AppCommandType.BrightnessDown => StepBrightness(request.StepValue, direction: -1),
                AppCommandType.SettingsShow => ShowSettings(),
                AppCommandType.MonitorsRefresh => RefreshMonitors(),
                AppCommandType.AutoOn => ToggleAutoBrightness(enabled: true),
                AppCommandType.AutoOff => ToggleAutoBrightness(enabled: false),
                AppCommandType.EyeProtectionOn => ToggleEyeProtection(enabled: true, request.DurationHours),
                AppCommandType.EyeProtectionOff => ToggleEyeProtection(enabled: false, durationHours: null),
                AppCommandType.BoostOn => ToggleBrightnessBoost(enabled: true, request.DurationHours),
                AppCommandType.BoostOff => ToggleBrightnessBoost(enabled: false, durationHours: null),
                AppCommandType.Status => GetStatus(),
                AppCommandType.AppExit => ExitApp(),
                _ => CommandResponse.Error(CliExitCode.InvalidArguments, "Unsupported BrightSync command.")
            };
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Log.Warning(ex, "Resident command rejected for {CommandType}", request.CommandType);
            return CommandResponse.Error(CliExitCode.InvalidArguments, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Resident command handling failed for {CommandType}", request.CommandType);
            return CommandResponse.Error(CliExitCode.ResidentCommandFailed, "BrightSync failed to process the command.");
        }
    }

    private CommandResponse SetBrightness(int? brightnessValue)
    {
        if (!brightnessValue.HasValue || brightnessValue.Value is < 0 or > 100)
            return CommandResponse.Error(CliExitCode.InvalidArguments, "Brightness set requires a value from 0 to 100.");

        var value = brightnessValue.Value;
        return _engine.TrySetUserBrightness(value)
            ? CommandResponse.Ok($"Brightness set to {_engine.MasterBrightness}%.", _engine.MasterBrightness)
            : CommandResponse.Error(CliExitCode.ManualCommandBlockedByAutoBrightness,
                "Automatic brightness is enabled. Disable it before using manual brightness commands.");
    }

    private CommandResponse StepBrightness(int? stepValue, int direction)
    {
        if (!stepValue.HasValue || stepValue.Value is < 1 or > 100)
            return CommandResponse.Error(CliExitCode.InvalidArguments, "Brightness step requires a value from 1 to 100.");

        var target = Math.Clamp(_engine.MasterBrightness + (direction * stepValue.Value), 0, 100);
        return _engine.TrySetUserBrightness(target)
            ? CommandResponse.Ok($"Brightness set to {_engine.MasterBrightness}%.", _engine.MasterBrightness)
            : CommandResponse.Error(CliExitCode.ManualCommandBlockedByAutoBrightness,
                "Automatic brightness is enabled. Disable it before using manual brightness commands.");
    }

    private CommandResponse ShowSettings()
    {
        _showSettings();
        return CommandResponse.Ok("BrightSync settings opened.");
    }

    private CommandResponse RefreshMonitors()
    {
        _refreshMonitors();
        return CommandResponse.Ok("BrightSync monitor refresh requested.");
    }

    private CommandResponse ToggleAutoBrightness(bool enabled)
    {
        _autoBrightnessService.SetEnabled(enabled);
        return CommandResponse.Ok($"Automatic brightness {(enabled ? "enabled" : "disabled")}.");
    }

    private CommandResponse ToggleEyeProtection(bool enabled, int? durationHours)
    {
        _eyeProtectionService.SetEnabled(enabled, durationHours);
        return CommandResponse.Ok($"Eye protection {(enabled ? "enabled" : "disabled")}.");
    }

    private CommandResponse ToggleBrightnessBoost(bool enabled, int? durationHours)
    {
        _brightnessBoostService.SetEnabled(enabled, durationHours);
        return CommandResponse.Ok($"Brightness boost {(enabled ? "enabled" : "disabled")}.");
    }

    private CommandResponse GetStatus()
    {
        var snapshot = _getStatusSnapshot();
        return CommandResponse.Ok(snapshot.ToDisplayString(), status: snapshot);
    }

    private CommandResponse ExitApp()
    {
        _requestAppExit();
        return CommandResponse.Ok("BrightSync exit requested.");
    }

    private CliStatusSnapshot CreateFallbackStatusSnapshot()
        => CliStatusSnapshotFactory.Create(
            _engine.MasterBrightness,
            _autoBrightnessService.IsEnabled,
            _eyeProtectionService.IsEnabled,
            _eyeProtectionService.EndTimeUtc,
            _brightnessBoostService.IsEnabled,
            _brightnessBoostService.EndTimeUtc,
            monitorCount: 0,
            controllableMonitorCount: 0,
            timestampUtc: DateTimeOffset.UtcNow,
            version: "unknown");
}
