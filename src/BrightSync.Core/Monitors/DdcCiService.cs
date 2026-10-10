using BrightSync.Core.Config;
using BrightSync.Platform;
using Serilog;

namespace BrightSync.Core.Monitors;

/// <summary>
/// Enumerates physical monitors and exposes DDC/CI brightness get/set operations.
/// Thread-safe for concurrent brightness writes from the sync engine.
/// </summary>
public sealed class DdcCiService : IDisposable
{
    private readonly ConfigManager _config;
    private readonly object _lock = new();
    private readonly object _refreshLock = new();
    private readonly IMonitorBackend _backend;
    private readonly IDdcMonitorProvider _monitorProvider;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private List<DdcMonitor> _monitors = new();
    private List<IDisposable> _resources = new();
    private MonitorDisplaySnapshot[] _monitorDisplaySnapshots = [];
    private TaskCompletionSource? _refreshCompletion;
    private int _disposed;
    private int _disposing;
    private bool _refreshInProgress;
    private bool _refreshRequested;

    public DdcCiService(ConfigManager config)
        : this(config, monitorProvider: null)
    {
    }

    internal DdcCiService(ConfigManager config, IDdcMonitorProvider? monitorProvider)
        : this(config, monitorProvider, PlatformServices.Current.Monitors)
    {
    }

    internal DdcCiService(ConfigManager config, IDdcMonitorProvider? monitorProvider, IMonitorBackend backend)
    {
        _config = config;
        _backend = backend;
        _monitorProvider = monitorProvider ?? backend;
        Refresh();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    private bool IsStopping => Volatile.Read(ref _disposing) != 0;

    /// <summary>Re-enumerates all connected monitors. Call after display topology changes.</summary>
    public void Refresh()
    {
        TaskCompletionSource completion;
        var execute = false;

        lock (_refreshLock)
        {
            if (IsDisposed || IsStopping)
                return;

            _refreshRequested = true;
            if (_refreshInProgress)
            {
                completion = _refreshCompletion!;
            }
            else
            {
                _refreshInProgress = true;
                _refreshRequested = false;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _refreshCompletion = completion;
                execute = true;
            }
        }

        if (execute)
        {
            ExecuteRefreshLoop(completion);
            return;
        }

        // A concurrent caller waits for the active pass and any coalesced
        // follow-up pass, preserving the synchronous behavior of Refresh().
        completion.Task.GetAwaiter().GetResult();
    }

    /// <summary>Returns a snapshot of currently known DDC/CI monitors.</summary>
    public IReadOnlyList<DdcMonitor> GetMonitors()
    {
        lock (_lock)
            return IsDisposed || IsStopping ? [] : _monitors.ToList();
    }

    /// <summary>
    /// Returns display metadata without waiting for monitor hardware operations.
    /// The snapshot is replaced atomically after a monitor refresh completes.
    /// </summary>
    public IReadOnlyList<MonitorDisplaySnapshot> GetMonitorDisplaySnapshot()
        => IsStopping ? [] : Volatile.Read(ref _monitorDisplaySnapshots);

    /// <summary>
    /// Sets brightness on a specific monitor. brightness is a percentage 0–100.
    /// Maps the percentage to the monitor's native DDC range.
    /// </summary>
    public bool SetBrightness(DdcMonitor monitor, int brightnessPercent)
    {
        brightnessPercent = Math.Clamp(brightnessPercent, 0, 100);
        // Acquire the lock so a concurrent Refresh() cannot destroy the handle mid-call.
        lock (_lock)
        {
            if (!IsCurrentMonitorLocked(monitor)) return false;
            var ok = _backend.SetBrightness(monitor, brightnessPercent);

            if (ok)
            {
                monitor.LastCommandedPercent = brightnessPercent;
                Log.Debug("Set monitor brightness. Monitor={Monitor}, Brightness={Brightness}%, Backend={Backend}",
                    monitor.FriendlyName, brightnessPercent, monitor.BrightnessBackend);
            }
            else
            {
                Log.Warning("Brightness update failed. Monitor={Monitor}, Brightness={Brightness}%, Backend={Backend}",
                    monitor.FriendlyName, brightnessPercent, monitor.BrightnessBackend);
            }

            return ok;
        }
    }

    /// <summary>
    /// Reads current brightness from the monitor via DDC/CI.
    /// This call is slow (~40–150 ms per monitor) — avoid on the UI thread.
    /// </summary>
    public bool TryGetBrightness(DdcMonitor monitor, out int brightnessPercent)
    {
        brightnessPercent = 0;
        lock (_lock)
        {
            if (!IsCurrentMonitorLocked(monitor)) return false;
            var ok = _backend.TryGetBrightness(monitor, out brightnessPercent);

            if (!ok)
            {
                Log.Debug("Unable to read current brightness for monitor {Monitor} using backend {Backend}",
                    monitor.FriendlyName,
                    monitor.BrightnessBackend);
            }

            return ok;
        }
    }

    // --- Private ---

    public bool SetVcpFeature(DdcMonitor monitor, byte vcpCode, uint value)
    {
        lock (_lock)
        {
            return IsCurrentMonitorLocked(monitor) &&
                   !monitor.IsInternal &&
                   _backend.SetVcpFeature(monitor, vcpCode, value);
        }
    }

    public bool GetVcpFeature(DdcMonitor monitor, byte vcpCode, out uint currentValue, out uint maxValue)
    {
        currentValue = 0;
        maxValue = 0;
        lock (_lock)
        {
            return IsCurrentMonitorLocked(monitor) &&
                   !monitor.IsInternal &&
                   _backend.GetVcpFeature(monitor, vcpCode, out currentValue, out maxValue);
        }
    }

    private void ExecuteRefreshLoop(TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                try
                {
                    RefreshCore(_lifetimeCts.Token);
                }
                catch (OperationCanceledException) when (IsDisposed || IsStopping)
                {
                    Log.Debug("DDC/CI refresh canceled during service disposal");
                }
                catch (Exception ex)
                {
                    // Refresh can run from timer/event threads. Keep failures
                    // observed and preserve the last known-good monitor set.
                    Log.Error(ex, "DDC/CI monitor refresh failed");
                }

                lock (_refreshLock)
                {
                    if (IsDisposed || !_refreshRequested)
                    {
                        _refreshRequested = false;
                        _refreshInProgress = false;
                        if (ReferenceEquals(_refreshCompletion, completion))
                            _refreshCompletion = null;
                        completion.TrySetResult();
                        return;
                    }

                    // Consume the request and make exactly one follow-up pass.
                    _refreshRequested = false;
                }
            }
        }
        catch (Exception ex)
        {
            // Keep the coordinator itself exception-contained even if a future
            // change fails outside the per-pass refresh boundary.
            Log.Error(ex, "Unexpected DDC/CI refresh coordinator failure");
            lock (_refreshLock)
            {
                _refreshRequested = false;
                _refreshInProgress = false;
                if (ReferenceEquals(_refreshCompletion, completion))
                    _refreshCompletion = null;
            }

            completion.TrySetResult();
        }
    }

    private void RefreshCore(CancellationToken cancellationToken)
    {
        if (IsDisposed || IsStopping || cancellationToken.IsCancellationRequested)
            return;

        _backend.InvalidateCaches();
        cancellationToken.ThrowIfCancellationRequested();

        DdcMonitorSet? candidate = null;
        try
        {
            var useLegacyDetection = _config.Config.UseLegacyDdcCiDetection;
            var refreshedMonitors = _monitorProvider.Enumerate(useLegacyDetection, cancellationToken);
            candidate = refreshedMonitors;
            cancellationToken.ThrowIfCancellationRequested();

            lock (_lock)
            {
                if (IsDisposed || IsStopping || cancellationToken.IsCancellationRequested)
                    return;

                var newMonitors = refreshedMonitors.Monitors.ToList();
                var newResources = refreshedMonitors.Resources.ToList();
                MonitorIdentityResolver.EnsureUniqueIdentities(newMonitors);

                var oldResources = _resources;
                _resources = newResources;
                _monitors = newMonitors;
                // Ownership transfers to _resources once the candidate is published.
                candidate = null;

                // Publish only immutable display metadata. The quick UI can read
                // this while hardware enumeration for a later pass is in flight.
                Volatile.Write(ref _monitorDisplaySnapshots, newMonitors
                    .Select(m => new MonitorDisplaySnapshot(
                        m.DeviceName,
                        m.ManufacturerName,
                        m.ModelName,
                        m.FriendlyName,
                        m.Description,
                        m.SupportsDdcCi)
                    {
                        StableIdentity = m.StableIdentity
                    })
                    .ToArray());

                DisposeResources(oldResources);

                Log.Information(
                    "DDC/CI refresh finished. DetectionMode={DetectionMode}, TotalMonitors={TotalMonitors}, ControllableMonitors={ControllableMonitors}",
                    useLegacyDetection ? "Legacy" : "Modern",
                    newMonitors.Count,
                    newMonitors.Count(m => m.SupportsDdcCi));
            }

        }
        finally
        {
            candidate?.DisposeResources();
        }
    }

    private bool IsCurrentMonitorLocked(DdcMonitor monitor)
    {
        if (monitor is null || IsDisposed || IsStopping)
            return false;

        // GetMonitors intentionally returns shallow monitor objects for existing
        // callers. Reference membership makes every object from a replaced
        // snapshot fail before its native handle can be used.
        return _monitors.Any(current => ReferenceEquals(current, monitor));
    }

    private void DisposeResources()
    {
        DisposeResources(_resources);
        _resources.Clear();
    }

    public void Dispose()
    {
        lock (_refreshLock)
        {
            if (IsDisposed || Interlocked.Exchange(ref _disposing, 1) != 0)
                return;

            _refreshRequested = false;
            _lifetimeCts.Cancel();
        }

        Log.Debug("Disposing DDC/CI service");
        lock (_lock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            DisposeResources();
            _monitors = new List<DdcMonitor>();
            Volatile.Write(ref _monitorDisplaySnapshots, []);
        }
    }

    private static void DisposeResources(IEnumerable<IDisposable> resources)
    {
        foreach (var resource in resources)
        {
            try
            {
                resource.Dispose();
            }
            catch
            {
                /* best-effort cleanup */
            }
        }
    }
}

/// <summary>Immutable monitor metadata used by UI surfaces that do not need native handles.</summary>
public sealed record MonitorDisplaySnapshot(
    string DeviceName,
    string ManufacturerName,
    string ModelName,
    string FriendlyName,
    string Description,
    bool SupportsDdcCi)
{
    public string StableIdentity { get; init; } = string.Empty;
}
