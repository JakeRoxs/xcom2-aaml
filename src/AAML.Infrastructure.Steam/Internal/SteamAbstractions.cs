namespace AAML.Infrastructure.Steam.Internal;

internal interface ISteamClientApi
{
    SteamInitialization Initialize();
    void RunCallbacks();
    void Shutdown();
}

internal sealed record SteamInitialization(bool IsSuccess, string Code, string Diagnostic);
internal readonly record struct SteamQueryHandle(ulong Value) { public bool IsValid => Value != 0; }
internal readonly record struct SteamAsyncCall(ulong Value) { public bool IsValid => Value != 0; }
internal sealed record SteamQueryCompletion(SteamQueryHandle Handle, bool IoFailure, bool IsSuccess, uint ResultCount, string Diagnostic);
internal sealed record SteamWorkshopSnapshot(ulong PublishedFileId, string Title, IReadOnlyList<ulong> ChildIds, string? Description = null, ulong OwnerSteamId = 0, string? Tags = null, bool TagsTruncated = false, uint CreatedAt = 0, uint UpdatedAt = 0, uint AddedAt = 0, string? PreviewUrl = null);
internal sealed record SteamInstallSnapshot(ulong SizeOnDisk, string Folder, uint UnixTimestamp);
internal sealed record SteamDownloadSnapshot(ulong BytesDownloaded, ulong BytesTotal);
internal enum SteamSubscriptionMutation { Subscribe, Unsubscribe }
internal sealed record SteamMutationCompletion(ulong PublishedFileId, bool IoFailure, bool IsSuccess, string Diagnostic);

internal interface ISteamUgcApi
{
    SteamQueryHandle CreateDetailsQuery(IReadOnlyList<ulong> ids);
    bool SetReturnChildren(SteamQueryHandle handle, bool enabled);
    bool SetReturnLongDescription(SteamQueryHandle handle, bool enabled);
    SteamAsyncCall SendQuery(SteamQueryHandle handle);
    bool TryGetQueryItem(SteamQueryHandle handle, uint index, out SteamWorkshopSnapshot item);
    bool ReleaseQuery(SteamQueryHandle handle);
    IReadOnlyList<ulong> GetSubscribedItems();
    uint GetItemState(ulong id);
    bool TryGetInstallInfo(ulong id, out SteamInstallSnapshot install);
    bool TryGetDownloadInfo(ulong id, out SteamDownloadSnapshot download);
    bool DownloadItem(ulong id, bool highPriority);
    SteamAsyncCall SubscribeItem(ulong id) => default;
    SteamAsyncCall UnsubscribeItem(ulong id) => default;
}

internal interface ISteamCallbacks
{
    Task<SteamQueryCompletion> WaitForQueryAsync(SteamAsyncCall call, CancellationToken cancellationToken);
    Task<string?> GetPersonaNameAsync(ulong steamId, CancellationToken cancellationToken);
    Task<SteamMutationCompletion> WaitForSubscriptionMutationAsync(SteamAsyncCall call, SteamSubscriptionMutation mutation, CancellationToken cancellationToken) => Task.FromResult(new SteamMutationCompletion(0, false, false, "Unsupported"));
}
