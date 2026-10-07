using BrightSync.Core.Brightness;
using BrightSync.Core.Config;
using BrightSync.Core.Monitors;

namespace BrightSync.Tests;

public sealed class MonitorLifecycleTests
{
    [Fact]
    public void StaleMonitorSnapshots_AreRejectedByAllCommandPaths()
    {
        var first = CreateMonitor("first");
        var second = CreateMonitor("second");
        var provider = new FakeMonitorProvider((call, _) =>
            new DdcMonitorSet(call == 1 ? new[] { first } : new[] { second }));
        using var ddc = CreateDdc(provider);

        var staleSnapshot = Assert.Single(ddc.GetMonitors());
        ddc.Refresh();

        Assert.False(ddc.SetBrightness(staleSnapshot, 50));
        Assert.False(ddc.TryGetBrightness(staleSnapshot, out _));
        Assert.False(ddc.SetVcpFeature(staleSnapshot, 0x10, 1));
        Assert.False(ddc.GetVcpFeature(staleSnapshot, 0x10, out _, out _));
        Assert.Same(second, Assert.Single(ddc.GetMonitors()));
    }

    [Fact]
    public void MonitorDisplaySnapshot_preserves_stable_identity_for_ui_profile_lookups()
    {
        var monitor = CreateMonitor("first");
        monitor.StableIdentity = "edid:DEL4141|INSTANCE-A";
        var provider = new FakeMonitorProvider((_, _) => new DdcMonitorSet(new[] { monitor }));
        using var ddc = CreateDdc(provider);

        var snapshot = Assert.Single(ddc.GetMonitorDisplaySnapshot());

        Assert.Equal(monitor.StableIdentity, snapshot.StableIdentity);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndRejectsRefreshAndCommands()
    {
        var monitor = CreateMonitor("only");
        var provider = new FakeMonitorProvider((_, _) => new DdcMonitorSet(new[] { monitor }));
        using var ddc = CreateDdc(provider);
        var snapshot = Assert.Single(ddc.GetMonitors());

        ddc.Dispose();
        ddc.Dispose();
        ddc.Refresh();

        Assert.Equal(1, provider.CallCount);
        Assert.Empty(ddc.GetMonitors());
        Assert.Empty(ddc.GetMonitorDisplaySnapshot());
        Assert.False(ddc.SetBrightness(snapshot, 50));
        Assert.False(ddc.TryGetBrightness(snapshot, out _));
        Assert.False(ddc.SetVcpFeature(snapshot, 0x10, 1));
    }

    [Fact]
    public async Task DisposeDuringRefresh_DiscardsCandidateAndCompletesWithoutThrowing()
    {
        var initial = CreateMonitor("initial");
        var candidate = CreateMonitor("candidate");
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeMonitorProvider((call, _) =>
        {
            if (call == 2)
            {
                refreshStarted.SetResult();
                releaseRefresh.Task.GetAwaiter().GetResult();
            }

            return new DdcMonitorSet(new[] { call == 1 ? initial : candidate });
        });
        using var ddc = CreateDdc(provider);

        var refreshTask = Task.Run(ddc.Refresh);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        ddc.Dispose();
        releaseRefresh.SetResult();

        await refreshTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(ddc.GetMonitors());
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public void RefreshProviderExceptions_AreContainedAndCurrentSnapshotIsRetained()
    {
        var current = CreateMonitor("current");
        var provider = new FakeMonitorProvider((call, _) =>
        {
            if (call == 2)
                throw new InvalidOperationException("fake enumeration failure");

            return new DdcMonitorSet(new[] { current });
        });
        using var ddc = CreateDdc(provider);

        var exception = Record.Exception(ddc.Refresh);

        Assert.Null(exception);
        Assert.Same(current, Assert.Single(ddc.GetMonitors()));
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task ConcurrentRefreshRequests_RunOneFollowUpPass()
    {
        var first = CreateMonitor("first");
        var active = CreateMonitor("active");
        var followUp = CreateMonitor("follow-up");
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeMonitorProvider((call, _) =>
        {
            if (call == 2)
            {
                refreshStarted.SetResult();
                releaseRefresh.Task.GetAwaiter().GetResult();
            }

            return new DdcMonitorSet(new[]
            {
                call == 1 ? first : call == 2 ? active : followUp
            });
        });
        using var ddc = CreateDdc(provider);

        var refreshTasks = new[]
        {
            Task.Run(ddc.Refresh),
            Task.Run(ddc.Refresh),
            Task.Run(ddc.Refresh)
        };
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        releaseRefresh.SetResult();

        await Task.WhenAll(refreshTasks).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(3, provider.CallCount);
        Assert.Same(followUp, Assert.Single(ddc.GetMonitors()));
    }

    [Fact]
    public async Task DelayedRefresh_IsCanceledByDispose()
    {
        var provider = new FakeMonitorProvider((_, _) => new DdcMonitorSet(new[] { CreateMonitor("only") }));
        using var ddc = CreateDdc(provider);
        using var engine = new BrightSyncEngine(ddc, new InternalBrightnessWatcher(), new ConfigManager());

        var delayedRefresh = engine.ScheduleRefreshAfterDelay(TimeSpan.FromSeconds(5));
        engine.Dispose();

        await delayedRefresh.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task DelayedRefresh_ContainsProviderExceptions()
    {
        var provider = new FakeMonitorProvider((call, _) =>
        {
            if (call == 2)
                throw new InvalidOperationException("fake enumeration failure");

            return new DdcMonitorSet(new[] { CreateMonitor("only") });
        });
        using var ddc = CreateDdc(provider);
        using var engine = new BrightSyncEngine(ddc, new InternalBrightnessWatcher(), new ConfigManager());

        var delayedRefresh = engine.ScheduleRefreshAfterDelay(TimeSpan.Zero);

        await delayedRefresh.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, provider.CallCount);
    }

    private static ConfigManager CreateConfig()
        => new(
            Path.Combine(
                Path.GetTempPath(),
                "BrightSyncTests",
                Guid.NewGuid().ToString("N"),
                "config.json"),
            _ => { });

    private static DdcCiService CreateDdc(FakeMonitorProvider provider)
        => new(CreateConfig(), provider);

    private static DdcMonitor CreateMonitor(string deviceName)
        => new()
        {
            DeviceName = deviceName,
            FriendlyName = deviceName,
            Description = deviceName,
            SupportsDdcCi = true,
            MaxDdcBrightness = 100,
            BrightnessBackend = "fake"
        };

    private sealed class FakeMonitorProvider(
        Func<int, CancellationToken, DdcMonitorSet> enumerate) : IDdcMonitorProvider
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public DdcMonitorSet Enumerate(bool useLegacyDetection, CancellationToken cancellationToken)
            => enumerate(Interlocked.Increment(ref _callCount), cancellationToken);
    }
}
