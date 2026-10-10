using Avalonia.Threading;
using BrightSync.Core;

namespace BrightSync.UI;

/// <summary>Routes core work onto Avalonia's UI thread.</summary>
internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => Dispatcher.UIThread.Invoke(action);

    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
        => await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
}
