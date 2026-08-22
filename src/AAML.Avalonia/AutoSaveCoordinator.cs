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
    private readonly Dictionary<string, HashSet<CancellationTokenSource>> active = [];
    private string? activeOwner;
    private bool enabled;
    private bool disposed;

    public void Register(string owner, Func<bool> isDirty, Func<CancellationToken, Task<Result>> save, Func<CancellationToken, Task<bool>>? isDirtyAsync = null) =>
        registrations[owner] = new Registration(isDirty, isDirtyAsync ?? (_ => Task.FromResult(isDirty())), save);

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

    public async Task<Result> FlushActiveAsync(CancellationToken cancellationToken)
    {
        if (activeOwner is not { } owner || registrations.GetValueOrDefault(owner) is not { } registration) return Result.Success();
        return await registration.IsDirtyAsync(cancellationToken).ConfigureAwait(false)
            ? await FlushAsync(owner, cancellationToken).ConfigureAwait(false)
            : Result.Success();
    }

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
        CancellationTokenSource? pendingSource;
        CancellationTokenSource[] activeSources;
        lock (sync)
        {
            pendingSource = pending.GetValueOrDefault(owner);
            activeSources = active.TryGetValue(owner, out var sources) ? sources.ToArray() : [];
            pending.Remove(owner);
        }
        CancelSource(pendingSource);
        foreach (var source in activeSources.Where(source => !ReferenceEquals(source, pendingSource))) CancelSource(source);
    }

    public async Task CancelAndWaitAsync(string owner, CancellationToken cancellationToken)
    {
        Cancel(owner);
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        saveGate.Release();
    }

    private async Task RunDelayedAsync(string owner, CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(DebounceDelay, source.Token).ConfigureAwait(false);
            lock (sync)
            {
                if (pending.GetValueOrDefault(owner) == source) pending.Remove(owner);
                source.Token.ThrowIfCancellationRequested();
                if (!active.TryGetValue(owner, out var sources)) active[owner] = sources = [];
                sources.Add(source);
            }

            if (!registrations.TryGetValue(owner, out var registration)) return;
            if (!await registration.IsDirtyAsync(source.Token).ConfigureAwait(false)) return;
            await registration.Save(source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // Save cancellation is expected when a newer change supersedes the pending debounce window.
        }
        finally
        {
            lock (sync)
            {
                if (pending.GetValueOrDefault(owner) == source) pending.Remove(owner);
                if (active.TryGetValue(owner, out var sources))
                {
                    sources.Remove(source);
                    if (sources.Count == 0) active.Remove(owner);
                }
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
        string[] activeOwners;
        lock (sync) activeOwners = active.Keys.ToArray();
        foreach (var owner in owners.Concat(activeOwners).Distinct(StringComparer.Ordinal)) Cancel(owner);
        saveGate.Dispose();
    }

    private static void CancelSource(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The source may already be disposed if another cancellation race wins.
        }
    }

    private sealed record Registration(Func<bool> IsDirty, Func<CancellationToken, Task<bool>> IsDirtyAsync, Func<CancellationToken, Task<Result>> Save);
}
