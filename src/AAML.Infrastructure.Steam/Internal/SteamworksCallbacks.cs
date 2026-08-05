using Steamworks;

namespace AAML.Infrastructure.Steam.Internal;

internal sealed class SteamworksCallbacks : ISteamCallbacks, IDisposable
{
    private readonly object gate = new();
    private readonly HashSet<IDisposable> pending = [];
    private bool disposed;

    public Task<SteamQueryCompletion> WaitForQueryAsync(SteamAsyncCall call, CancellationToken cancellationToken)
    {
        if (!call.IsValid)
        {
            throw new ArgumentException("The Steam call handle is invalid.", nameof(call));
        }

        var completion = new TaskCompletionSource<SteamQueryCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        CallResult<SteamUGCQueryCompleted_t>? registration = null;
        CancellationTokenRegistration cancellationRegistration = default;
        registration = CallResult<SteamUGCQueryCompleted_t>.Create((result, ioFailure) =>
        {
            completion.TrySetResult(new SteamQueryCompletion(
                new SteamQueryHandle(result.m_handle.m_UGCQueryHandle),
                ioFailure,
                result.m_eResult == EResult.k_EResultOK,
                result.m_unNumResultsReturned,
                result.m_eResult.ToString()));
            Complete(registration!, cancellationRegistration);
        });

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending.Add(registration);
            registration.Set(new SteamAPICall_t(call.Value));
            cancellationRegistration = cancellationToken.Register(() =>
            {
                completion.TrySetCanceled(cancellationToken);
                Complete(registration!, cancellationRegistration);
            });
        }

        return completion.Task;
    }

    public Task<string?> GetPersonaNameAsync(ulong steamId, CancellationToken cancellationToken)
    {
        if (steamId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(steamId));
        }

        var nativeId = new CSteamID(steamId);
        if (!SteamFriends.RequestUserInformation(nativeId, true))
        {
            return Task.FromResult<string?>(SteamFriends.GetFriendPersonaName(nativeId));
        }

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Callback<PersonaStateChange_t>? callback = null;
        CancellationTokenRegistration cancellationRegistration = default;
        callback = Callback<PersonaStateChange_t>.Create(change =>
        {
            if (change.m_ulSteamID != steamId)
            {
                return;
            }

            completion.TrySetResult(SteamFriends.GetFriendPersonaName(nativeId));
            Complete(callback!, cancellationRegistration);
        });

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending.Add(callback);
            cancellationRegistration = cancellationToken.Register(() =>
            {
                completion.TrySetCanceled(cancellationToken);
                Complete(callback!, cancellationRegistration);
            });
        }

        return completion.Task;
    }

    public Task<SteamMutationCompletion> WaitForSubscriptionMutationAsync(SteamAsyncCall call, SteamSubscriptionMutation mutation, CancellationToken cancellationToken) => mutation == SteamSubscriptionMutation.Subscribe
        ? WaitMutationAsync<RemoteStorageSubscribePublishedFileResult_t>(call, result => (result.m_nPublishedFileId.m_PublishedFileId, result.m_eResult), cancellationToken)
        : WaitMutationAsync<RemoteStorageUnsubscribePublishedFileResult_t>(call, result => (result.m_nPublishedFileId.m_PublishedFileId, result.m_eResult), cancellationToken);

    private Task<SteamMutationCompletion> WaitMutationAsync<T>(SteamAsyncCall call, Func<T, (ulong Id, EResult Result)> map, CancellationToken cancellationToken)
    {
        if (!call.IsValid) throw new ArgumentException("The Steam call handle is invalid.", nameof(call));
        var completion = new TaskCompletionSource<SteamMutationCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        CallResult<T>? registration = null; CancellationTokenRegistration cancellationRegistration = default;
        registration = CallResult<T>.Create((result, ioFailure) => { var mapped = map(result); completion.TrySetResult(new SteamMutationCompletion(mapped.Id, ioFailure, mapped.Result == EResult.k_EResultOK, mapped.Result.ToString())); Complete(registration!, cancellationRegistration); });
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); pending.Add(registration); registration.Set(new SteamAPICall_t(call.Value)); cancellationRegistration = cancellationToken.Register(() => { completion.TrySetCanceled(cancellationToken); Complete(registration!, cancellationRegistration); }); }
        return completion.Task;
    }

    public void Dispose()
    {
        IDisposable[] registrations;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            registrations = pending.ToArray();
            pending.Clear();
        }

        foreach (var registration in registrations)
        {
            registration.Dispose();
        }
    }

    private void Complete(IDisposable registration, CancellationTokenRegistration cancellationRegistration)
    {
        lock (gate)
        {
            pending.Remove(registration);
        }

        cancellationRegistration.Dispose();
        registration.Dispose();
    }
}
