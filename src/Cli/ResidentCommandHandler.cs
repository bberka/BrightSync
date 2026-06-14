using Avalonia.Threading;
using BrightSync.Core.Brightness;
using BrightSync.UI;
using Serilog;

namespace BrightSync.Cli;

public sealed class ResidentCommandHandler(
    BrightSyncEngine engine,
    AutoBrightnessService autoBrightnessService,
    EyeProtectionService eyeProtectionService,
    BrightnessBoostService brightnessBoostService,
    TrayManager trayManager,
    Action requestAppExit)
{
    public async Task<CommandResponse> HandleAsync(CommandRequest request, CancellationToken cancellationToken)
        => await Dispatcher.UIThread.InvokeAsync(() => HandleCore(request), DispatcherPriority.Normal, cancellationToken);

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
                AppCommandType.AppExit => ExitApp(),
                _ => CommandResponse.Error(CliExitCode.InvalidArguments, "Unsupported BrightSync command.")
            };
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
        return engine.TrySetUserBrightness(value)
            ? CommandResponse.Ok($"Brightness set to {engine.MasterBrightness}%.", engine.MasterBrightness)
            : CommandResponse.Error(CliExitCode.ManualCommandBlockedByAutoBrightness,
                "Automatic brightness is enabled. Disable it before using manual brightness commands.");
    }

    private CommandResponse StepBrightness(int? stepValue, int direction)
    {
        if (!stepValue.HasValue || stepValue.Value is < 1 or > 100)
            return CommandResponse.Error(CliExitCode.InvalidArguments, "Brightness step requires a value from 1 to 100.");

        var target = Math.Clamp(engine.MasterBrightness + (direction * stepValue.Value), 0, 100);
        return engine.TrySetUserBrightness(target)
            ? CommandResponse.Ok($"Brightness set to {engine.MasterBrightness}%.", engine.MasterBrightness)
            : CommandResponse.Error(CliExitCode.ManualCommandBlockedByAutoBrightness,
                "Automatic brightness is enabled. Disable it before using manual brightness commands.");
    }

    private CommandResponse ShowSettings()
    {
        trayManager.ShowSettings();
        return CommandResponse.Ok("BrightSync settings opened.");
    }

    private CommandResponse RefreshMonitors()
    {
        trayManager.RefreshMonitorsFromCommand();
        return CommandResponse.Ok("BrightSync monitor refresh requested.");
    }

    private CommandResponse ToggleAutoBrightness(bool enabled)
    {
        autoBrightnessService.SetEnabled(enabled);
        return CommandResponse.Ok($"Automatic brightness {(enabled ? "enabled" : "disabled")}.");
    }

    private CommandResponse ToggleEyeProtection(bool enabled, int? durationHours)
    {
        eyeProtectionService.SetEnabled(enabled, durationHours);
        return CommandResponse.Ok($"Eye protection {(enabled ? "enabled" : "disabled")}.");
    }

    private CommandResponse ToggleBrightnessBoost(bool enabled, int? durationHours)
    {
        brightnessBoostService.SetEnabled(enabled, durationHours);
        return CommandResponse.Ok($"Brightness boost {(enabled ? "enabled" : "disabled")}.");
    }

    private CommandResponse ExitApp()
    {
        requestAppExit();
        return CommandResponse.Ok("BrightSync exit requested.");
    }
}
