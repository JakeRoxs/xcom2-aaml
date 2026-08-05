using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Domain.Mods;

namespace AAML.Application.Mods.Workshop;

public sealed record WorkshopMutationOutcome(WorkshopId WorkshopId, Result SubscriptionOutcome, Result? DownloadRequestOutcome = null)
{
    public Result Outcome => !SubscriptionOutcome.IsSuccess
        ? SubscriptionOutcome
        : DownloadRequestOutcome is { IsSuccess: false } ? DownloadRequestOutcome.Value : Result.Success();
    public bool Subscribed => SubscriptionOutcome.IsSuccess;
    public bool DownloadRequested => DownloadRequestOutcome?.IsSuccess == true;
}
public sealed record WorkshopMutationResult(IReadOnlyList<WorkshopMutationOutcome> Items)
{
    public bool IsSuccess => Items.All(item => item.Outcome.IsSuccess);
    public bool IsPartialSuccess => Items.Any(item => item.Outcome.IsSuccess) && !IsSuccess;
}

public interface IWorkshopSubscriptionCoordinator
{
    Task<WorkshopMutationResult> SubscribeAsync(IReadOnlyCollection<WorkshopId> ids, CancellationToken cancellationToken);
    Task<Result<(ApplicationSettings Settings, WorkshopMutationResult Mutations)>> UnsubscribeRetainingIntentAsync(ApplicationSettings settings, IReadOnlyList<ModInstallation> installations, IReadOnlySet<ModKey> selected, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> RemoveRetainedIntentAsync(ApplicationSettings settings, WorkshopId id, CancellationToken cancellationToken);
}

public sealed class WorkshopSubscriptionCoordinator(IWorkshopService workshop, ISettingsRepository repository) : IWorkshopSubscriptionCoordinator
{
    public async Task<WorkshopMutationResult> SubscribeAsync(IReadOnlyCollection<WorkshopId> ids, CancellationToken cancellationToken)
    {
        var outcomes = new List<WorkshopMutationOutcome>();
        foreach (var id in ids.Where(id => id.Value > 0).Distinct().OrderBy(id => id.Value))
        {
            if (cancellationToken.IsCancellationRequested) { outcomes.Add(new(id, Result.Failure(new Error("workshop.subscription_cancelled", "Subscription operation was cancelled.", ErrorKind.Cancelled)))); continue; }
            var result = await workshop.SubscribeAsync(id, cancellationToken).ConfigureAwait(false);
            Result? download = result.IsSuccess
                ? await workshop.RequestDownloadAsync(id, true, cancellationToken).ConfigureAwait(false)
                : null;
            outcomes.Add(new(id, result, download));
        }
        return new WorkshopMutationResult(outcomes);
    }

    public async Task<Result<(ApplicationSettings Settings, WorkshopMutationResult Mutations)>> UnsubscribeRetainingIntentAsync(ApplicationSettings settings, IReadOnlyList<ModInstallation> installations, IReadOnlySet<ModKey> selected, CancellationToken cancellationToken)
    {
        var candidates = installations.Where(mod => selected.Contains(mod.Key) && mod.WorkshopId.HasValue).ToArray();
        if (candidates.Length == 0) return Result<(ApplicationSettings, WorkshopMutationResult)>.Failure(new Error("workshop.selection_empty", "Select at least one Workshop mod.", ErrorKind.Validation));
        var outcomes = new List<WorkshopMutationOutcome>(); var original = (settings.RetainedWorkshopItems ?? []).ToDictionary(item => item.WorkshopId); var retained = original.ToDictionary();
        var unique = candidates.GroupBy(mod => mod.WorkshopId!.Value).Select(group => group.First()).OrderBy(mod => mod.WorkshopId!.Value.Value).ToArray();
        foreach (var mod in unique) retained[mod.WorkshopId!.Value] = new RetainedWorkshopItem(mod.WorkshopId.Value, mod.PackageId, mod.Name, mod.Key);
        var prepared = settings with { RetainedWorkshopItems = retained.Values.OrderBy(item => item.WorkshopId.Value).ToArray() };
        var preparedSave = await repository.SaveAsync(prepared, cancellationToken).ConfigureAwait(false);
        if (!preparedSave.IsSuccess) return Result<(ApplicationSettings, WorkshopMutationResult)>.Failure(preparedSave.Error!);
        foreach (var mod in unique)
        {
            var result = await workshop.UnsubscribeAsync(mod.WorkshopId!.Value, cancellationToken).ConfigureAwait(false);
            outcomes.Add(new(mod.WorkshopId.Value, result));
            if (!result.IsSuccess) { if (original.TryGetValue(mod.WorkshopId.Value, out var prior)) retained[mod.WorkshopId.Value] = prior; else retained.Remove(mod.WorkshopId.Value); }
        }
        var updated = settings with { RetainedWorkshopItems = retained.Values.OrderBy(item => item.WorkshopId.Value).ToArray() };
        var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<(ApplicationSettings, WorkshopMutationResult)>.Success((updated, new WorkshopMutationResult(outcomes))) : Result<(ApplicationSettings, WorkshopMutationResult)>.Failure(saved.Error!);
    }

    public async Task<Result<ApplicationSettings>> RemoveRetainedIntentAsync(ApplicationSettings settings, WorkshopId id, CancellationToken cancellationToken)
    {
        if (!(settings.RetainedWorkshopItems ?? []).Any(item => item.WorkshopId == id))
            return Result<ApplicationSettings>.Failure(new Error("workshop.retained_intent_missing", "The retained Workshop intent no longer exists.", ErrorKind.NotFound));
        var retained = (settings.RetainedWorkshopItems ?? []).Where(item => item.WorkshopId != id).ToArray();
        var removedKeys = (settings.RetainedWorkshopItems ?? []).Where(item => item.WorkshopId == id).Select(item => item.LastKnownKey).ToHashSet();
        var updated = settings with { RetainedWorkshopItems = retained, ModIntents = settings.ModIntents.Where(intent => !removedKeys.Contains(intent.Mod)).ToArray() };
        var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ApplicationSettings>.Success(updated) : Result<ApplicationSettings>.Failure(saved.Error!);
    }
}
