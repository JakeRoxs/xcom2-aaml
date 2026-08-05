using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Mods;
using AAML.Infrastructure.Steam.Internal;

namespace AAML.Infrastructure.Steam;

/// <summary>Maps Steam UGC operations to application-owned Workshop contracts.</summary>
public sealed class SteamWorkshopService : IWorkshopService
{
    private readonly SteamClientLifetime lifetime;
    private readonly ISteamUgcApi ugc;
    private readonly ISteamCallbacks callbacks;
    private readonly SteamOptions options;

    internal SteamWorkshopService(SteamClientLifetime lifetime, ISteamUgcApi ugc, ISteamCallbacks callbacks, SteamOptions options)
    {
        this.lifetime = lifetime;
        this.ugc = ugc;
        this.callbacks = callbacks;
        this.options = options;
    }

    public async Task<Result<WorkshopItem?>> GetItemAsync(WorkshopId publishedFileId, CancellationToken cancellationToken)
    {
        if (publishedFileId.Value == 0)
        {
            return Result<WorkshopItem?>.Failure(new Error("steam.invalid_id", "Workshop ID must be nonzero.", ErrorKind.Validation));
        }

        var result = await GetItemsAsync([publishedFileId], null, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Result<WorkshopItem?>.Success(result.Value!.FirstOrDefault())
            : Result<WorkshopItem?>.Failure(result.Error!);
    }

    public async Task<Result<IReadOnlyList<WorkshopItem>>> GetItemsAsync(
        IReadOnlyList<WorkshopId> publishedFileIds,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publishedFileIds);
        if (publishedFileIds.Any(id => id.Value == 0))
        {
            return Result<IReadOnlyList<WorkshopItem>>.Failure(new Error("steam.invalid_id", "Workshop IDs must be nonzero.", ErrorKind.Validation));
        }

        var requested = publishedFileIds.Distinct().ToArray();
        if (requested.Length == 0)
        {
            return Result<IReadOnlyList<WorkshopItem>>.Success([]);
        }

        var started = await lifetime.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return Result<IReadOnlyList<WorkshopItem>>.Failure(started.Error!);
        }

        var mapped = new Dictionary<ulong, WorkshopItem>();
        var completed = 0;
        foreach (var chunk in requested.Chunk(options.QueryBatchSize))
        {
            var result = await QueryAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return Result<IReadOnlyList<WorkshopItem>>.Failure(result.Error!);
            }

            foreach (var item in result.Value!)
            {
                mapped[item.PublishedFileId.Value] = item;
                completed++;
                progress?.Report(new OperationProgress("steam.query.details", completed, requested.Length));
            }
        }

        return Result<IReadOnlyList<WorkshopItem>>.Success(requested.Where(id => mapped.ContainsKey(id.Value)).Select(id => mapped[id.Value]).ToArray());
    }

    public async Task<Result<IReadOnlyList<WorkshopId>>> GetSubscribedItemsAsync(CancellationToken cancellationToken)
    {
        var started = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return Result<IReadOnlyList<WorkshopId>>.Failure(started.Error!);
        }

        try
        {
            return Result<IReadOnlyList<WorkshopId>>.Success(ugc.GetSubscribedItems().Where(id => id != 0).Distinct().Select(id => new WorkshopId(id)).ToArray());
        }
        catch (Exception exception)
        {
            return Result<IReadOnlyList<WorkshopId>>.Failure(new Error("steam.subscriptions_failed", exception.Message, ErrorKind.ExternalService));
        }
    }

    public async Task<Result<WorkshopLocalState>> GetLocalStateAsync(WorkshopId publishedFileId, CancellationToken cancellationToken)
    {
        if (publishedFileId.Value == 0)
        {
            return Result<WorkshopLocalState>.Failure(new Error("steam.invalid_id", "Workshop ID must be nonzero.", ErrorKind.Validation));
        }

        var started = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return Result<WorkshopLocalState>.Failure(started.Error!);
        }

        try
        {
            var install = ugc.TryGetInstallInfo(publishedFileId.Value, out var installSnapshot)
                ? new WorkshopInstallInfo(installSnapshot.SizeOnDisk, installSnapshot.Folder, SteamValueConversions.FromUnixTimestamp(installSnapshot.UnixTimestamp))
                : null;
            var download = ugc.TryGetDownloadInfo(publishedFileId.Value, out var downloadSnapshot)
                ? new WorkshopDownloadInfo(downloadSnapshot.BytesDownloaded, downloadSnapshot.BytesTotal, SteamValueConversions.DownloadFraction(downloadSnapshot.BytesDownloaded, downloadSnapshot.BytesTotal))
                : null;
            return Result<WorkshopLocalState>.Success(new WorkshopLocalState(publishedFileId, (WorkshopItemState)ugc.GetItemState(publishedFileId.Value), install, download));
        }
        catch (Exception exception)
        {
            return Result<WorkshopLocalState>.Failure(new Error("steam.local_state_failed", exception.Message, ErrorKind.ExternalService));
        }
    }

    public async Task<Result> RequestDownloadAsync(WorkshopId publishedFileId, bool highPriority, CancellationToken cancellationToken)
    {
        if (publishedFileId.Value == 0)
        {
            return Result.Failure(new Error("steam.invalid_id", "Workshop ID must be nonzero.", ErrorKind.Validation));
        }

        var started = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return started;
        }
        if (cancellationToken.IsCancellationRequested)
            return Result.Failure(new Error("steam.download_cancelled", "The download request was cancelled before it was sent to Steam.", ErrorKind.Cancelled));
        try
        {
            return ugc.DownloadItem(publishedFileId.Value, highPriority)
                ? Result.Success()
                : Result.Failure(new Error("steam.download_rejected", "Steam rejected the download request.", ErrorKind.ExternalService));
        }
        catch (Exception exception)
        {
            return Result.Failure(new Error("steam.download_failed", exception.Message, ErrorKind.ExternalService));
        }
    }

    public Task<Result> SubscribeAsync(WorkshopId publishedFileId, CancellationToken cancellationToken) => MutateSubscriptionAsync(publishedFileId, SteamSubscriptionMutation.Subscribe, cancellationToken);
    public Task<Result> UnsubscribeAsync(WorkshopId publishedFileId, CancellationToken cancellationToken) => MutateSubscriptionAsync(publishedFileId, SteamSubscriptionMutation.Unsubscribe, cancellationToken);

    private async Task<Result> MutateSubscriptionAsync(WorkshopId id, SteamSubscriptionMutation mutation, CancellationToken cancellationToken)
    {
        if (id.Value == 0) return Result.Failure(new Error("steam.invalid_id", "Workshop ID must be nonzero.", ErrorKind.Validation));
        var started = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false); if (!started.IsSuccess) return started;
        try
        {
            var call = mutation == SteamSubscriptionMutation.Subscribe ? ugc.SubscribeItem(id.Value) : ugc.UnsubscribeItem(id.Value);
            if (!call.IsValid) return Result.Failure(new Error("steam.subscription_rejected", "Steam rejected the subscription request.", ErrorKind.ExternalService));
            using var timeout = new CancellationTokenSource(options.QueryTimeout); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, lifetime.StoppingToken);
            var completed = await callbacks.WaitForSubscriptionMutationAsync(call, mutation, linked.Token).ConfigureAwait(false);
            return !completed.IoFailure && completed.IsSuccess && completed.PublishedFileId == id.Value ? Result.Success() : Result.Failure(new Error("steam.subscription_failed", completed.Diagnostic, ErrorKind.ExternalService));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Result.Failure(new Error("steam.subscription_cancelled", "Subscription monitoring was cancelled; Steam may still complete an accepted request.", ErrorKind.Cancelled)); }
        catch (OperationCanceledException) { return Result.Failure(new Error("steam.subscription_timeout", "Steam did not confirm the subscription change before timeout.", ErrorKind.Timeout)); }
        catch (Exception exception) { return Result.Failure(new Error("steam.subscription_failed", exception.Message, ErrorKind.ExternalService)); }
    }

    public async Task<Result<string?>> GetPersonaNameAsync(ulong steamId, CancellationToken cancellationToken)
    {
        if (steamId == 0)
        {
            return Result<string?>.Failure(new Error("steam.invalid_user", "Steam user ID must be nonzero.", ErrorKind.Validation));
        }

        var started = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return Result<string?>.Failure(started.Error!);
        }

        using var timeout = new CancellationTokenSource(options.QueryTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, lifetime.StoppingToken);
        try
        {
            return Result<string?>.Success(await callbacks.GetPersonaNameAsync(steamId, linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<string?>.Failure(new Error("steam.persona_cancelled", "The persona request was cancelled.", ErrorKind.Cancelled));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Result<string?>.Failure(new Error("steam.persona_timeout", "The persona request timed out.", ErrorKind.Timeout));
        }
        catch (Exception exception)
        {
            return Result<string?>.Failure(new Error("steam.persona_failed", exception.Message, ErrorKind.ExternalService));
        }
    }

    private async Task<Result<IReadOnlyList<WorkshopItem>>> QueryAsync(IReadOnlyList<WorkshopId> ids, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        var handle = ugc.CreateDetailsQuery(ids.Select(id => id.Value).ToArray());
        if (!handle.IsValid)
        {
            return Failure("steam.query_invalid_handle", "Steam did not create a valid query.", ErrorKind.ExternalService);
        }

        Result<IReadOnlyList<WorkshopItem>> result;
        try
        {
            if (!ugc.SetReturnChildren(handle, true) || !ugc.SetReturnLongDescription(handle, true))
            {
                result = Failure("steam.query_configuration_failed", "Steam rejected query options.", ErrorKind.ExternalService);
            }
            else
            {
                var call = ugc.SendQuery(handle);
                if (!call.IsValid)
                {
                    result = Failure("steam.query_send_failed", "Steam did not create a valid asynchronous call.", ErrorKind.ExternalService);
                }
                else
                {
                    result = await AwaitAndMapAsync(handle, call, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (!ugc.ReleaseQuery(handle))
            {
                result = Failure("steam.query_cleanup_failed", "Steam query cleanup failed.", ErrorKind.ExternalService);
            }
        }

        return result;
    }

    private async Task<Result<IReadOnlyList<WorkshopItem>>> AwaitAndMapAsync(SteamQueryHandle handle, SteamAsyncCall call, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.QueryTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, lifetime.StoppingToken);
        try
        {
            var completion = await callbacks.WaitForQueryAsync(call, linked.Token).ConfigureAwait(false);
            if (completion.IoFailure || !completion.IsSuccess || completion.Handle != handle)
            {
                return Failure("steam.query_failed", completion.Diagnostic, ErrorKind.ExternalService);
            }

            var items = new List<WorkshopItem>();
            for (uint index = 0; index < completion.ResultCount; index++)
            {
                if (!ugc.TryGetQueryItem(handle, index, out var snapshot) || snapshot.PublishedFileId == 0)
                {
                    return Failure("steam.query_invalid_data", "Steam returned malformed Workshop data.", ErrorKind.InvalidData);
                }

                items.Add(new WorkshopItem(
                    new WorkshopId(snapshot.PublishedFileId),
                    snapshot.Title,
                    snapshot.ChildIds.Where(id => id != 0).Distinct().Select(id => new WorkshopId(id)).ToArray(),
                    snapshot.Description,
                    snapshot.OwnerSteamId == 0 ? null : snapshot.OwnerSteamId,
                    (snapshot.Tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    snapshot.TagsTruncated,
                    Time(snapshot.CreatedAt), Time(snapshot.UpdatedAt), Time(snapshot.AddedAt),
                    ValidPreviewUrl(snapshot.PreviewUrl)));
            }

            return Result<IReadOnlyList<WorkshopItem>>.Success(items);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Failure("steam.query_timeout", "The Steam query timed out.", ErrorKind.Timeout);
        }
        catch (OperationCanceledException)
        {
            return Failure("steam.stopping", "Steam is shutting down.", ErrorKind.Unavailable);
        }
    }

    private static DateTimeOffset? Time(uint unix) => unix == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(unix);
    private static string? ValidPreviewUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;

    private static Result<IReadOnlyList<WorkshopItem>> Cancelled() => Failure("steam.query_cancelled", "The Steam query was cancelled.", ErrorKind.Cancelled);
    private static Result<IReadOnlyList<WorkshopItem>> Failure(string code, string message, ErrorKind kind) => Result<IReadOnlyList<WorkshopItem>>.Failure(new Error(code, message, kind));

    private async Task<Result> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Result.Failure(new Error("steam.cancelled", "The Steam operation was cancelled.", ErrorKind.Cancelled));
        }

        return await lifetime.StartAsync(cancellationToken).ConfigureAwait(false);
    }
}
