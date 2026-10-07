using System.Reflection;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;
using Xunit;

namespace BrightSync.Tests;

[Trait("Category", "Hardware")]
[Trait("Category", "Integration")]
public class MonitorSnapshotTests
{
    [Fact]
    public async Task MonitorDisplaySnapshot_DoesNotWaitForHardwareLock()
    {
        var configManager = new ConfigManager();
        using var ddc = new DdcCiService(configManager);

        var lockField = typeof(DdcCiService).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(lockField);
        var monitorLock = lockField!.GetValue(ddc);
        Assert.NotNull(monitorLock);

        var lockHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockHolder = Task.Run(() =>
        {
            lock (monitorLock!)
            {
                lockHeld.SetResult();
                releaseLock.Task.GetAwaiter().GetResult();
            }
        });

        try
        {
            await lockHeld.Task.WaitAsync(TimeSpan.FromSeconds(1));

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var snapshotTask = Task.Run(() =>
            {
                started.SetResult();
                return ddc.GetMonitorDisplaySnapshot();
            });

            await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var completedTask = await Task.WhenAny(snapshotTask, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(snapshotTask, completedTask);
            Assert.NotNull(await snapshotTask);
        }
        finally
        {
            releaseLock.TrySetResult();
            await lockHolder;
        }
    }
}
