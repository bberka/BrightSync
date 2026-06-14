using BrightSync.Cli;

namespace BrightSync.Tests;

public sealed class CliCommandRouterTests
{
    [Fact]
    public async Task Route_returns_resident_result_when_resident_handles_command()
    {
        var router = new CliCommandRouter(
            new FakeResidentCommandClient(ResidentCommandDispatchResult.Success(
                CliExecutionResult.Success("Brightness set to 55%."))),
            new FakeOneShotCommandExecutor(CliExecutionResult.Success("one-shot")));

        var result = await router.RouteAsync(new AppCommand(AppCommandType.BrightnessSet, brightnessValue: 55),
            CancellationToken.None);

        Assert.Equal(CliExitCode.Success, result.ExitCode);
        Assert.Equal("Brightness set to 55%.", result.Message);
    }

    [Fact]
    public async Task Route_runs_one_shot_when_resident_is_not_running_and_command_is_local_capable()
    {
        var oneShotExecutor = new FakeOneShotCommandExecutor(CliExecutionResult.Success("Brightness set to 60%."));
        var router = new CliCommandRouter(
            new FakeResidentCommandClient(ResidentCommandDispatchResult.NotRunning()),
            oneShotExecutor);

        var result = await router.RouteAsync(new AppCommand(AppCommandType.BrightnessSet, brightnessValue: 60),
            CancellationToken.None);

        Assert.Equal(CliExitCode.Success, result.ExitCode);
        Assert.Equal(1, oneShotExecutor.ExecutionCount);
    }

    [Fact]
    public async Task Route_fails_when_resident_only_command_has_no_running_resident()
    {
        var router = new CliCommandRouter(
            new FakeResidentCommandClient(ResidentCommandDispatchResult.NotRunning()),
            new FakeOneShotCommandExecutor(CliExecutionResult.Success("unused")));

        var result = await router.RouteAsync(new AppCommand(AppCommandType.SettingsShow), CancellationToken.None);

        Assert.Equal(CliExitCode.ResidentAppRequired, result.ExitCode);
        Assert.Equal("BrightSync must already be running for this command.", result.Message);
    }

    [Fact]
    public async Task Route_surfaces_resident_failure_without_running_one_shot()
    {
        var oneShotExecutor = new FakeOneShotCommandExecutor(CliExecutionResult.Success("unused"));
        var router = new CliCommandRouter(
            new FakeResidentCommandClient(ResidentCommandDispatchResult.Failed(
                CliExecutionResult.Failure(CliExitCode.TransportFailure, "auth failed"))),
            oneShotExecutor);

        var result = await router.RouteAsync(new AppCommand(AppCommandType.BrightnessSet, brightnessValue: 44),
            CancellationToken.None);

        Assert.Equal(CliExitCode.TransportFailure, result.ExitCode);
        Assert.Equal(0, oneShotExecutor.ExecutionCount);
    }

    [Fact]
    public void DetermineTargetBrightness_calculates_set_up_and_down_targets()
    {
        Assert.Equal(75, OneShotCommandExecutor.DetermineTargetBrightness(
            new AppCommand(AppCommandType.BrightnessSet, brightnessValue: 75), 40));
        Assert.Equal(60, OneShotCommandExecutor.DetermineTargetBrightness(
            new AppCommand(AppCommandType.BrightnessUp, stepValue: 10), 50));
        Assert.Equal(30, OneShotCommandExecutor.DetermineTargetBrightness(
            new AppCommand(AppCommandType.BrightnessDown, stepValue: 20), 50));
    }

    private sealed class FakeResidentCommandClient(ResidentCommandDispatchResult result) : IResidentCommandClient
    {
        public Task<ResidentCommandDispatchResult> TryDispatchAsync(AppCommand command, CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    private sealed class FakeOneShotCommandExecutor(CliExecutionResult result) : IOneShotCommandExecutor
    {
        public int ExecutionCount { get; private set; }

        public Task<CliExecutionResult> ExecuteAsync(AppCommand command, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(result);
        }
    }
}
