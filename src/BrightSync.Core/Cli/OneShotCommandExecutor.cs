using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;

namespace BrightSync.Cli;

public sealed class OneShotCommandExecutor : IOneShotCommandExecutor
{
    public Task<CliExecutionResult> ExecuteAsync(AppCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configManager = new ConfigManager();
        using var ddcService = new DdcCiService(configManager);
        using var watcher = new InternalBrightnessWatcher();
        using var engine = new BrightSyncEngine(ddcService, watcher, configManager);
        engine.Start();

        var targetBrightness = DetermineTargetBrightness(command, engine.MasterBrightness);
        var changed = engine.TrySetUserBrightnessSync(targetBrightness);
        if (!changed)
        {
            return Task.FromResult(
                CliExecutionResult.Failure(
                    CliExitCode.ManualCommandBlockedByAutoBrightness,
                    "Automatic brightness is enabled. Disable it before using manual brightness commands."));
        }

        return Task.FromResult(CliExecutionResult.Success($"Brightness set to {engine.MasterBrightness}%."));
    }

    internal static int DetermineTargetBrightness(AppCommand command, int currentBrightness)
        => command.CommandType switch
        {
            AppCommandType.BrightnessSet => command.BrightnessValue ?? currentBrightness,
            AppCommandType.BrightnessUp => Math.Clamp(currentBrightness + (command.StepValue ?? 0), 0, 100),
            AppCommandType.BrightnessDown => Math.Clamp(currentBrightness - (command.StepValue ?? 0), 0, 100),
            _ => currentBrightness
        };
}
