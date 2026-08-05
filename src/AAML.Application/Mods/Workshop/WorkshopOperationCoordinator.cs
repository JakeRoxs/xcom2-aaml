using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Mods;

namespace AAML.Application.Mods.Workshop;

public sealed record WorkshopModState(ModKey Mod, WorkshopId WorkshopId, UpdateStatus Update, WorkshopItemState RawState, WorkshopInstallInfo? Install, WorkshopDownloadInfo? Download);
public sealed record WorkshopModOutcome(ModKey Mod, WorkshopId WorkshopId, WorkshopModState? State, Result Outcome);
public sealed record WorkshopBatchResult(IReadOnlyList<WorkshopModOutcome> Items, bool ObservationCancelled = false)
{
    public bool IsSuccess => Items.All(item => item.Outcome.IsSuccess);
    public bool IsPartialSuccess => Items.Any(item => item.Outcome.IsSuccess) && Items.Any(item => !item.Outcome.IsSuccess);
}
public sealed record WorkshopOperationProgress(string Operation, int CompletedItems, int TotalItems, ulong BytesDownloaded, ulong? BytesTotal, ModKey? CurrentMod, WorkshopModState? State = null);
public sealed record WorkshopDownloadOptions(TimeSpan PollInterval, TimeSpan ItemTimeout, bool HighPriority)
{
    public static WorkshopDownloadOptions Default { get; } = new(TimeSpan.FromMilliseconds(500), TimeSpan.FromMinutes(20), true);
}

public interface IWorkshopOperationCoordinator
{
    Task<WorkshopBatchResult> RefreshAsync(IReadOnlyList<ModInstallation> installations, IProgress<WorkshopOperationProgress>? progress, CancellationToken cancellationToken);
    Task<WorkshopBatchResult> DownloadUpdatesAsync(IReadOnlyList<ModInstallation> installations, WorkshopDownloadOptions options, IProgress<WorkshopOperationProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>Coordinates bounded Workshop state and download workflows without exposing Steam lifecycle details.</summary>
public sealed class WorkshopOperationCoordinator(IWorkshopService workshop, TimeProvider? timeProvider = null) : IWorkshopOperationCoordinator
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<WorkshopBatchResult> RefreshAsync(IReadOnlyList<ModInstallation> installations, IProgress<WorkshopOperationProgress>? progress, CancellationToken cancellationToken)
    {
        var projection = Project(installations);
        var states = new Dictionary<WorkshopId, (WorkshopLocalState? State, Error? Error)>();
        var completed = 0;
        foreach (var item in projection.ByWorkshop.OrderBy(pair => pair.Key.Value))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                states[item.Key] = (null, Cancelled(false));
                continue;
            }
            var result = await workshop.GetLocalStateAsync(item.Key, cancellationToken).ConfigureAwait(false);
            states[item.Key] = result.IsSuccess ? (result.Value!, null) : (null, result.Error!);
            completed += item.Value.Count;
            progress?.Report(new WorkshopOperationProgress("workshop.refresh", completed, projection.Mods.Count, result.Value?.Download?.BytesDownloaded ?? 0, result.Value?.Download?.BytesTotal, item.Value[0].Key, result.IsSuccess ? Map(item.Value[0].Key, result.Value!) : null));
        }
        return FanOut(projection, states, cancellationToken.IsCancellationRequested);
    }

    public async Task<WorkshopBatchResult> DownloadUpdatesAsync(IReadOnlyList<ModInstallation> installations, WorkshopDownloadOptions options, IProgress<WorkshopOperationProgress>? progress, CancellationToken cancellationToken)
    {
        var projection = Project(installations);
        if (options.PollInterval <= TimeSpan.Zero || options.ItemTimeout <= TimeSpan.Zero)
            return FailAll(projection, new Error("workshop.options_invalid", "Polling interval and item timeout must be positive.", ErrorKind.Validation));

        var outcomes = new Dictionary<WorkshopId, (WorkshopLocalState? State, Error? Error)>();
        var pending = new Dictionary<WorkshopId, DateTimeOffset>();
        var observedBytes = new Dictionary<WorkshopId, (ulong Downloaded, ulong? Total)>();
        foreach (var item in projection.ByWorkshop.OrderBy(pair => pair.Key.Value))
        {
            if (cancellationToken.IsCancellationRequested) { outcomes[item.Key] = (null, Cancelled(false)); continue; }
            var local = await workshop.GetLocalStateAsync(item.Key, cancellationToken).ConfigureAwait(false);
            if (!local.IsSuccess) { outcomes[item.Key] = (null, local.Error!); continue; }
            var initialState = local.Value!;
            outcomes[item.Key] = (initialState, null);
            ObserveBytes(observedBytes, item.Key, initialState);
            progress?.Report(new WorkshopOperationProgress("workshop.download.state", 0, projection.ByWorkshop.Count, initialState.Download?.BytesDownloaded ?? 0, initialState.Download?.BytesTotal, item.Value[0].Key, Map(item.Value[0].Key, initialState)));
            if (IsComplete(initialState)) continue;
            var transient = initialState.State.HasFlag(WorkshopItemState.Downloading) || initialState.State.HasFlag(WorkshopItemState.DownloadPending);
            if (!transient)
            {
                var requested = await workshop.RequestDownloadAsync(item.Key, options.HighPriority, cancellationToken).ConfigureAwait(false);
                if (!requested.IsSuccess) { outcomes[item.Key] = (initialState, requested.Error!); continue; }
            }
            pending[item.Key] = time.GetUtcNow() + options.ItemTimeout;
        }

        while (pending.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                foreach (var id in pending.Keys) outcomes[id] = (outcomes[id].State, Cancelled(true));
                return FanOut(projection, outcomes, true);
            }

            foreach (var id in pending.Keys.OrderBy(id => id.Value).ToArray())
            {
                var local = await workshop.GetLocalStateAsync(id, cancellationToken).ConfigureAwait(false);
                if (!local.IsSuccess) { outcomes[id] = (outcomes[id].State, local.Error!); pending.Remove(id); continue; }
                var currentState = local.Value!;
                outcomes[id] = (currentState, null);
                ObserveBytes(observedBytes, id, currentState);
                progress?.Report(new WorkshopOperationProgress("workshop.download.state", projection.ByWorkshop.Count - pending.Count, projection.ByWorkshop.Count, currentState.Download?.BytesDownloaded ?? 0, currentState.Download?.BytesTotal, projection.ByWorkshop[id][0].Key, Map(projection.ByWorkshop[id][0].Key, currentState)));
                if (IsComplete(currentState)) pending.Remove(id);
                else if (time.GetUtcNow() >= pending[id])
                {
                    outcomes[id] = (currentState, Timeout(id, currentState));
                    pending.Remove(id);
                }
            }

            ReportProgress(projection, observedBytes, pending.Keys, progress);
            if (pending.Count == 0) break;
            try { await Task.Delay(options.PollInterval, time, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                foreach (var id in pending.Keys) outcomes[id] = (outcomes[id].State, Cancelled(true));
                return FanOut(projection, outcomes, true);
            }
        }

        ReportProgress(projection, observedBytes, [], progress);
        return FanOut(projection, outcomes, false);
    }

    private static Projection Project(IReadOnlyList<ModInstallation> installations)
    {
        var mods = installations.Where(mod => mod.WorkshopId is { Value: > 0 }).GroupBy(mod => mod.Key).Select(group => group.First()).ToArray();
        return new Projection(mods, mods.GroupBy(mod => mod.WorkshopId!.Value).ToDictionary(group => group.Key, group => (IReadOnlyList<ModInstallation>)group.ToArray()));
    }

    private static WorkshopBatchResult FanOut(Projection projection, IReadOnlyDictionary<WorkshopId, (WorkshopLocalState? State, Error? Error)> values, bool cancelled)
    {
        var items = projection.Mods.Select(mod =>
        {
            var id = mod.WorkshopId!.Value;
            var value = values[id];
            var state = value.State is null ? null : Map(mod.Key, value.State);
            return new WorkshopModOutcome(mod.Key, id, state, value.Error is null ? Result.Success() : Result.Failure(Contextualize(value.Error, mod.Key, id)));
        }).ToArray();
        return new WorkshopBatchResult(items, cancelled);
    }

    private static WorkshopBatchResult FailAll(Projection projection, Error error) => new(projection.Mods.Select(mod => new WorkshopModOutcome(mod.Key, mod.WorkshopId!.Value, null, Result.Failure(Contextualize(error, mod.Key, mod.WorkshopId.Value)))).ToArray());
    private static WorkshopModState Map(ModKey mod, WorkshopLocalState state) => new(mod, state.PublishedFileId, ToUpdateStatus(state.State), state.State, state.Install, state.Download);
    private static UpdateStatus ToUpdateStatus(WorkshopItemState state) => state.HasFlag(WorkshopItemState.Downloading) || state.HasFlag(WorkshopItemState.DownloadPending) ? UpdateStatus.Downloading : state.HasFlag(WorkshopItemState.NeedsUpdate) ? UpdateStatus.Available : state.HasFlag(WorkshopItemState.Installed) ? UpdateStatus.Current : UpdateStatus.Unknown;
    private static bool IsComplete(WorkshopLocalState state) => state.State.HasFlag(WorkshopItemState.Installed) && !state.State.HasFlag(WorkshopItemState.NeedsUpdate) && !state.State.HasFlag(WorkshopItemState.Downloading) && !state.State.HasFlag(WorkshopItemState.DownloadPending);

    private static void ObserveBytes(IDictionary<WorkshopId, (ulong Downloaded, ulong? Total)> observed, WorkshopId id, WorkshopLocalState state)
    {
        observed.TryGetValue(id, out var prior);
        var downloaded = Math.Max(prior.Downloaded, state.Download?.BytesDownloaded ?? 0);
        var total = state.Download?.BytesTotal is > 0 ? Math.Max(prior.Total ?? 0, state.Download.BytesTotal) : prior.Total;
        observed[id] = (downloaded, total);
    }

    private static void ReportProgress(Projection projection, IReadOnlyDictionary<WorkshopId, (ulong Downloaded, ulong? Total)> observed, IEnumerable<WorkshopId> pending, IProgress<WorkshopOperationProgress>? progress)
    {
        if (progress is null) return;
        var pendingIds = pending.ToHashSet();
        ulong downloaded = 0, total = 0;
        var totalKnown = true;
        foreach (var id in projection.ByWorkshop.Keys)
        {
            var bytes = observed.GetValueOrDefault(id);
            downloaded += bytes.Downloaded;
            if (bytes.Total is not { } known) totalKnown = false; else total += known;
        }
        var completed = projection.ByWorkshop.Count - pendingIds.Count;
        var current = pendingIds.OrderBy(id => id.Value).Select(id => projection.ByWorkshop[id][0].Key).FirstOrDefault();
        progress.Report(new WorkshopOperationProgress("workshop.download", completed, projection.ByWorkshop.Count, downloaded, totalKnown ? total : null, current == default ? null : current));
    }

    private static Error Cancelled(bool accepted) => new("workshop.monitoring_cancelled", accepted ? "Monitoring was cancelled. Steam may continue accepted downloads in the background." : "The Workshop operation was cancelled.", ErrorKind.Cancelled, new Dictionary<string, string> { ["requestAccepted"] = accepted.ToString(), ["steamDownloadMayContinue"] = accepted.ToString() });
    private static Error Timeout(WorkshopId id, WorkshopLocalState state) => new("workshop.download_timeout", "AAML stopped monitoring before Steam reported completion. Steam may continue in the background.", ErrorKind.Timeout, new Dictionary<string, string> { ["workshopId"] = id.Value.ToString(), ["lastState"] = state.State.ToString(), ["steamDownloadMayContinue"] = bool.TrueString });
    private static Error Contextualize(Error error, ModKey mod, WorkshopId id) => error with { Metadata = new Dictionary<string, string>(error.Metadata ?? new Dictionary<string, string>()) { ["modKey"] = mod.ToString(), ["workshopId"] = id.Value.ToString() } };
    private sealed record Projection(IReadOnlyList<ModInstallation> Mods, IReadOnlyDictionary<WorkshopId, IReadOnlyList<ModInstallation>> ByWorkshop);
}
