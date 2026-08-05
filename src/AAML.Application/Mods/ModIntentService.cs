using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Domain.Mods;

namespace AAML.Application.Mods;

public sealed record ModIntentEdit(ModKey Mod, bool IsActive, int? ExplicitOrder);

public interface IModIntentService
{
    Result<ApplicationSettings> Merge(ApplicationSettings settings, IReadOnlyList<ModIntentEdit> edits);
    Task<Result<ApplicationSettings>> SaveAsync(ApplicationSettings settings, IReadOnlyList<ModIntentEdit> edits, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> SetDependencyIgnoredAsync(ApplicationSettings settings, ModKey parent, WorkshopId required, bool ignored, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> RemoveAsync(ApplicationSettings settings, ModKey mod, CancellationToken cancellationToken);
}

/// <summary>Merges grid-owned activation/order edits without replacing notes, categories, tags, or dependency decisions.</summary>
public sealed class ModIntentService(ISettingsRepository repository) : IModIntentService
{
    public Result<ApplicationSettings> Merge(ApplicationSettings settings, IReadOnlyList<ModIntentEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Any(edit => edit.ExplicitOrder < 0))
            return Result<ApplicationSettings>.Failure(new Error("mods.order_invalid", "Mod load order cannot be negative.", ErrorKind.Validation));
        if (edits.Select(edit => edit.Mod).Distinct().Count() != edits.Count)
            return Result<ApplicationSettings>.Failure(new Error("mods.edit_duplicate", "A mod may only be edited once per save.", ErrorKind.Validation));

        var merged = settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var edit in edits)
        {
            if (merged.TryGetValue(edit.Mod, out var existing))
            {
                var updatedIntent = existing with { IsActive = edit.IsActive, ExplicitOrder = edit.ExplicitOrder };
                if (IsEmpty(updatedIntent)) merged.Remove(edit.Mod);
                else merged[edit.Mod] = updatedIntent;
            }
            else if (edit.IsActive || edit.ExplicitOrder.HasValue)
                merged[edit.Mod] = new ModUserIntent(edit.Mod, edit.IsActive, false, edit.ExplicitOrder, null, null, new HashSet<TagId>(), null, new HashSet<WorkshopId>());
        }
        return Result<ApplicationSettings>.Success(settings with { ModIntents = merged.Values.OrderBy(intent => intent.ExplicitOrder ?? int.MaxValue).ThenBy(intent => intent.Mod.LocationIdentity, StringComparer.Ordinal).ToArray() });
    }

    public async Task<Result<ApplicationSettings>> SaveAsync(ApplicationSettings settings, IReadOnlyList<ModIntentEdit> edits, CancellationToken cancellationToken)
    {
        var merged = Merge(settings, edits);
        if (!merged.IsSuccess) return merged;
        var updated = merged.Value!;
        var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ApplicationSettings>.Success(updated) : Result<ApplicationSettings>.Failure(saved.Error!);
    }

    public async Task<Result<ApplicationSettings>> SetDependencyIgnoredAsync(ApplicationSettings settings, ModKey parent, WorkshopId required, bool ignored, CancellationToken cancellationToken)
    {
        var merged = settings.ModIntents.ToDictionary(intent => intent.Mod);
        var intent = merged.GetValueOrDefault(parent) ?? new ModUserIntent(parent, false, false, null, null, null, new HashSet<TagId>(), null, new HashSet<WorkshopId>());
        var ignoredIds = intent.IgnoredDependencies.ToHashSet();
        if (ignored) ignoredIds.Add(required); else ignoredIds.Remove(required);
        var updatedIntent = intent with { IgnoredDependencies = ignoredIds };
        if (IsEmpty(updatedIntent)) merged.Remove(parent); else merged[parent] = updatedIntent;
        var updated = settings with { ModIntents = merged.Values.OrderBy(item => item.ExplicitOrder ?? int.MaxValue).ThenBy(item => item.Mod.LocationIdentity, StringComparer.Ordinal).ToArray() };
        var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ApplicationSettings>.Success(updated) : Result<ApplicationSettings>.Failure(saved.Error!);
    }

    public async Task<Result<ApplicationSettings>> RemoveAsync(ApplicationSettings settings, ModKey mod, CancellationToken cancellationToken)
    {
        var updated = settings with { ModIntents = settings.ModIntents.Where(intent => intent.Mod != mod).ToArray(), DuplicatePreferences = (settings.DuplicatePreferences ?? []).Where(preference => preference.PreferredInstallation != mod).ToArray() };
        var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ApplicationSettings>.Success(updated) : Result<ApplicationSettings>.Failure(saved.Error!);
    }

    private static bool IsEmpty(ModUserIntent intent) => !intent.IsActive && !intent.IsHidden && !intent.ExplicitOrder.HasValue && string.IsNullOrWhiteSpace(intent.ManualName) && intent.Category is null && intent.Tags.Count == 0 && string.IsNullOrWhiteSpace(intent.Note) && intent.IgnoredDependencies.Count == 0;
}
