using BrightSync.Platform;

namespace BrightSync.Core.Services;

/// <summary>Named-mutex single-instance guard. The name matches the Inno Setup AppMutex so installers detect a running app.</summary>
internal sealed class WindowsSingleInstanceGuard : ISingleInstanceGuard
{
    private const string MutexName = "BrightSync-SingleInstance-Mutex-Guid-9b3d-098c86e194a9";

    private Mutex? _mutex;
    private bool _owned;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);
        _owned = createdNew;
        return createdNew;
    }

    public void Dispose()
    {
        if (_mutex is null)
            return;

        if (_owned)
            _mutex.ReleaseMutex();

        _mutex.Dispose();
        _mutex = null;
    }
}
