namespace BrightSync.Core;

/// <summary>Marshals work onto the UI thread. The UI layer installs the real implementation at startup.</summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread and waits for it.</summary>
    void Invoke(Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread and returns its result.</summary>
    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken);
}

/// <summary>Holder for the process-wide <see cref="IUiDispatcher"/>. Defaults to running work inline.</summary>
public static class UiDispatcher
{
    public static IUiDispatcher Current { get; set; } = new InlineUiDispatcher();

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();

        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(action());
        }
    }
}

/// <summary>The resident app actions the command line can trigger. Implemented by the UI layer.</summary>
public interface IResidentAppHost
{
    void ShowSettings();
    void RefreshMonitorsFromCommand();
}
