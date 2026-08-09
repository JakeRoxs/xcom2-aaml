using Avalonia.Threading;

namespace AAML.Avalonia;

/// <summary>Executes UI-bound state changes on the application dispatcher.</summary>
public interface IUiDispatcher
{
    /// <summary>Executes an action synchronously on the UI thread.</summary>
    /// <param name="action">The UI-bound action to execute.</param>
    void Invoke(Action action);

    /// <summary>Executes an action asynchronously on the UI thread.</summary>
    /// <param name="action">The UI-bound action to execute.</param>
    /// <param name="cancellationToken">Cancels dispatch before the action starts.</param>
    Task InvokeAsync(Action action, CancellationToken cancellationToken);

    /// <summary>Executes a function asynchronously on the UI thread.</summary>
    /// <typeparam name="T">The function result type.</typeparam>
    /// <param name="action">The UI-bound function to execute.</param>
    /// <param name="cancellationToken">Cancels dispatch before the function starts.</param>
    /// <returns>The function result.</returns>
    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken);
}

/// <summary>Uses Avalonia's main dispatcher for UI-bound state changes.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Invoke(action);
    }

    /// <inheritdoc />
    public async Task InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Dispatcher.UIThread.CheckAccess()) action();
        else await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Default, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Dispatcher.UIThread.CheckAccess()
            ? action()
            : await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Default, cancellationToken);
    }
}
