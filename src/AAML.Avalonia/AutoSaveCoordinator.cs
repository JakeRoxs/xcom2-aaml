using AAML.Application.Common;

namespace AAML.Avalonia;

/// <summary>Serializes durable settings writes and coordinates section-owned debounced drafts.</summary>
public sealed class AutoSaveCoordinator : IDisposable
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);

    private readonly SemaphoreSlim saveGate = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<string, Registration> registrations = [];
    private readonly Dictionary<string, CancellationTokenSource> pending = [];
    private string? activeOwner;
    private bool enabled;
    private bool disposed;

    public void Register(string owner, Func<bool> isDirty, Func<CancellationToken, Task<Result>> save) =>
        registrations[owner] = new Registration(isDirty, save);

    public void SetEnabled(bool value)
    {
        enabled = value;
        if (!value)
        {
            string[] owners;
            lock (sync) owners = pending.Keys.ToArray();
            foreach (var owner in owners) Cancel(owner);
        }
    }

    public void Activate(string owner)
    {
        activeOwner = owner;
        if (enabled && registrations.GetValueOrDefault(owner)?.IsDirty() == true) _ = FlushAsync(owner, CancellationToken.None);
    }

    public void Changed(string owner, bool immediate = false)
    {
        if (!enabled || disposed || registrations.GetValueOrDefault(owner)?.IsDirty() != true) return;
        if (immediate) { _ = FlushAsync(owner, CancellationToken.None); return; }

        var source = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (sync)
        {
            previous = pending.GetValueOrDefault(owner);
            pending[owner] = source;
        }
        previous?.Cancel();
        previous?.Dispose();
        _ = RunDelayedAsync(owner, source);
    }

    public async Task<Result> FlushActiveAsync(CancellationToken cancellationToken) =>
        activeOwner is { } owner && registrations.GetValueOrDefault(owner)?.IsDirty() == true
            ? await FlushAsync(owner, cancellationToken).ConfigureAwait(false)
            : Result.Success();

    public async Task<Result> FlushAsync(string owner, CancellationToken cancellationToken)
    {
        Cancel(owner);
        if (!registrations.TryGetValue(owner, out var registration)) return Result.Success();
        return await registration.Save(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<T>> SerializeAsync<T>(Func<CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken)
    {
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation(cancellationToken).ConfigureAwait(false); }
        finally { saveGate.Release(); }
    }

    public async Task<Result> SerializeAsync(Func<CancellationToken, Task<Result>> operation, CancellationToken cancellationToken)
    {
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation(cancellationToken).ConfigureAwait(false); }
        finally { saveGate.Release(); }
    }

    public void Cancel(string owner)
    {
        CancellationTokenSource? source;
        lock (sync)
        {
            source = pending.GetValueOrDefault(owner);
            pending.Remove(owner);
        }
        source?.Cancel();
    }

    private async Task RunDelayedAsync(string owner, CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(DebounceDelay, source.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (pending.GetValueOrDefault(owner) == source) pending.Remove(owner);
            }
            if (registrations.TryGetValue(owner, out var registration) && registration.IsDirty())
                await registration.Save(source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        finally
        {
            lock (sync)
            {
                if (pending.GetValueOrDefault(owner) == source) pending.Remove(owner);
            }
            source.Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        string[] owners;
        lock (sync) owners = pending.Keys.ToArray();
        foreach (var owner in owners) Cancel(owner);
        saveGate.Dispose();
    }

    private sealed record Registration(Func<bool> IsDirty, Func<CancellationToken, Task<Result>> Save);
}
