using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using AAML.Application.Mods.Grid;
using AAML.Application.Settings;
using AAML.Application.Mods.Workshop;
using AAML.Domain.Mods;
using DynamicData;
using DynamicData.Binding;

namespace AAML.Avalonia;

internal sealed record ModProjectionEntry(
    ModInstallation Installation,
    ModGridItem Item,
    WorkshopModState? WorkshopState,
    string? WorkshopError);

internal sealed record ModProjectionContext(
    ModGridQuery Query,
    ModGridLookups Lookups,
    IReadOnlySet<ModGridGroupKey> CollapsedGroups,
    IReadOnlySet<ModKey> FocusedKeys,
    IReadOnlyList<RetainedWorkshopItem> RetainedItems,
    IReadOnlyDictionary<WorkshopId, string> RetainedStatuses);

internal sealed class ModProjectionStore : IDisposable
{
    private readonly SourceCache<ModProjectionEntry, ModKey> entries = new(entry => entry.Installation.Key);
    private readonly BehaviorSubject<ModProjectionContext> context;
    private readonly ObservableCollection<SessionModRow> rows;
    private readonly ObservableCollection<ModProjectionEntry> sortedEntries = [];
    private readonly Action<ModKey, bool, int?> updateDraft;
    private readonly Dictionary<ModProjectionRowKey, ProjectionSlot> registry = [];
    private readonly IDisposable subscription;
    private ModProjectionContext currentContext;
    private IReadOnlyList<ModProjectionRowDefinition>? pendingRows;
    private bool batching;

    public ModProjectionStore(ObservableCollection<SessionModRow> rows, Action<ModKey, bool, int?> updateDraft)
    {
        this.rows = rows;
        this.updateDraft = updateDraft;
        currentContext = EmptyContext();
        context = new BehaviorSubject<ModProjectionContext>(currentContext);
        var predicates = context.Select(value =>
        {
            var itemPredicate = ModGridProjector.CreatePredicate(value.Query, value.Lookups);
            return new Func<ModProjectionEntry, bool>(entry =>
                (value.FocusedKeys.Count == 0 || value.FocusedKeys.Contains(entry.Installation.Key)) && itemPredicate(entry.Item));
        });
        var comparers = context.Select(value =>
        {
            var itemComparer = ModGridProjector.CreateComparer(value.Query.Sort, value.Lookups);
            return Comparer<ModProjectionEntry>.Create((left, right) => itemComparer.Compare(left.Item, right.Item));
        });
        var rowSubscription = sortedEntries.ToObservableChangeSet()
            .ToCollection()
            .CombineLatest(context, BuildRows)
            .Subscribe(QueueRows);
        var bindSubscription = entries.Connect()
            .Filter(predicates)
            .SortAndBind(sortedEntries, comparers)
            .Subscribe();
        subscription = new CompositeDisposable(rowSubscription, bindSubscription);
    }

    public void Apply(IReadOnlyList<ModProjectionEntry> snapshot, ModProjectionContext nextContext)
    {
        batching = true;
        try
        {
            var desiredKeys = snapshot.Select(entry => entry.Installation.Key).ToHashSet();
            entries.Edit(updater =>
            {
                updater.RemoveKeys(updater.Keys.Where(key => !desiredKeys.Contains(key)).ToArray());
                foreach (var entry in snapshot)
                {
                    var existing = updater.Lookup(entry.Installation.Key);
                    if (!existing.HasValue || existing.Value != entry) updater.AddOrUpdate(entry);
                }
            });
            if (!ContextEquals(currentContext, nextContext))
            {
                currentContext = nextContext;
                context.OnNext(nextContext);
            }
        }
        finally
        {
            batching = false;
            if (pendingRows is { } pending)
            {
                pendingRows = null;
                ApplyRows(pending);
            }
        }
    }

    public void UpdateEntries(IReadOnlyList<ModProjectionEntry> updates)
    {
        entries.Edit(updater =>
        {
            foreach (var entry in updates)
            {
                var existing = updater.Lookup(entry.Installation.Key);
                if (!existing.HasValue || existing.Value != entry) updater.AddOrUpdate(entry);
            }
        });
    }

    private IReadOnlyList<ModProjectionRowDefinition> BuildRows(IReadOnlyCollection<ModProjectionEntry> filtered, ModProjectionContext value)
    {
        var result = new List<ModProjectionRowDefinition>(filtered.Count + value.RetainedItems.Count);
        if (value.Query.Grouping is ModGridGrouping.NoGrouping)
            result.AddRange(filtered.Select(entry => new ModProjectionRow(entry, ModGridSemanticStatePolicy.Calculate(entry.Item).ToString())));
        else
        {
            var byKey = filtered.ToDictionary(entry => entry.Installation.Key);
            var groupingQuery = value.Query with { SearchText = string.Empty, IncludeHidden = true, StateFilters = new HashSet<ModGridSemanticState>() };
            foreach (var row in ModGridProjector.Project(filtered.Select(entry => entry.Item), value.Lookups, groupingQuery, value.CollapsedGroups))
            {
                if (row is ModGridGroupRow group) result.Add(new GroupProjectionRow(group.Key, group.Label, group.ItemCount, group.IsExpanded));
                else if (row is ModGridModRow mod) result.Add(new ModProjectionRow(byKey[mod.Item.Key], mod.SemanticState.ToString()));
            }
        }
        var discoveredWorkshopIds = entries.Items.Where(entry => entry.Installation.WorkshopId.HasValue).Select(entry => entry.Installation.WorkshopId!.Value).ToHashSet();
        foreach (var retained in value.RetainedItems.Where(item => !discoveredWorkshopIds.Contains(item.WorkshopId)))
            result.Add(new RetainedProjectionRow(retained, value.RetainedStatuses.GetValueOrDefault(retained.WorkshopId)));
        return result;
    }

    private void QueueRows(IReadOnlyList<ModProjectionRowDefinition> projected)
    {
        if (batching) { pendingRows = projected; return; }
        ApplyRows(projected);
    }

    private void ApplyRows(IReadOnlyList<ModProjectionRowDefinition> projected)
    {
        var desiredKeys = projected.Select(definition => definition.Key).ToHashSet();
        for (var index = 0; index < projected.Count; index++)
        {
            var definition = projected[index];
            SessionModRow row;
            if (registry.TryGetValue(definition.Key, out var slot) && slot.Definition == definition) row = slot.Row;
            else if (slot is not null)
            {
                row = slot.Row;
                RefreshRow(row, definition);
                registry[definition.Key] = new(definition, row);
            }
            else
            {
                row = CreateRow(definition);
                registry[definition.Key] = new(definition, row);
            }

            if (index < rows.Count && ReferenceEquals(rows[index], row)) continue;
            var currentIndex = rows.IndexOf(row);
            if (currentIndex < 0) rows.Insert(index, row);
            else rows.Move(currentIndex, index);
        }
        while (rows.Count > projected.Count) rows.RemoveAt(rows.Count - 1);
        var cacheKeys = entries.Keys.Select(ModProjectionRowKey.Mod).ToHashSet();
        foreach (var key in registry.Keys.Where(key => !desiredKeys.Contains(key) && !cacheKeys.Contains(key)).ToArray()) registry.Remove(key);
    }

    private SessionModRow CreateRow(ModProjectionRowDefinition definition) => definition switch
    {
        GroupProjectionRow group => SessionModRow.Group(group.GroupKey, group.Name, group.Count, group.IsExpanded),
        ModProjectionRow mod => SessionModRow.Mod(mod.Entry.Item, mod.Entry.Installation, mod.State, mod.Entry.WorkshopState, mod.Entry.WorkshopError, updateDraft),
        RetainedProjectionRow retained => SessionModRow.Retained(retained.Item, retained.WorkshopStatus),
        _ => throw new ArgumentOutOfRangeException(nameof(definition))
    };

    private static void RefreshRow(SessionModRow row, ModProjectionRowDefinition definition)
    {
        switch (definition)
        {
            case GroupProjectionRow group: row.RefreshGroup(group.Name, group.Count, group.IsExpanded); break;
            case ModProjectionRow mod: row.RefreshMod(mod.Entry.Item, mod.Entry.Installation, mod.State, mod.Entry.WorkshopState, mod.Entry.WorkshopError); break;
            case RetainedProjectionRow retained: row.RefreshRetained(retained.Item, retained.WorkshopStatus); break;
            default: throw new ArgumentOutOfRangeException(nameof(definition));
        }
    }

    private static bool ContextEquals(ModProjectionContext left, ModProjectionContext right) =>
        left.Query.SearchText == right.Query.SearchText &&
        left.Query.IncludeHidden == right.Query.IncludeHidden &&
        left.Query.Sort == right.Query.Sort &&
        left.Query.Grouping == right.Query.Grouping &&
        left.Query.StateFilters.SetEquals(right.Query.StateFilters) &&
        left.Lookups.Categories.SequenceEqual(right.Lookups.Categories) &&
        left.Lookups.Tags.SequenceEqual(right.Lookups.Tags) &&
        left.CollapsedGroups.SetEquals(right.CollapsedGroups) &&
        left.FocusedKeys.SetEquals(right.FocusedKeys) &&
        left.RetainedItems.SequenceEqual(right.RetainedItems) &&
        left.RetainedStatuses.Count == right.RetainedStatuses.Count &&
        left.RetainedStatuses.All(pair => right.RetainedStatuses.GetValueOrDefault(pair.Key) == pair.Value);

    private static ModProjectionContext EmptyContext() => new(ModGridQuery.Default, new([], []), new HashSet<ModGridGroupKey>(), new HashSet<ModKey>(), [], new Dictionary<WorkshopId, string>());

    public void Dispose()
    {
        subscription.Dispose();
        context.Dispose();
        entries.Dispose();
    }

    private sealed record ProjectionSlot(ModProjectionRowDefinition Definition, SessionModRow Row);
}

internal readonly record struct ModProjectionRowKey(byte Kind, ModKey? ModKey, ModGridGroupKey? GroupKey, WorkshopId? WorkshopId)
{
    public static ModProjectionRowKey Mod(ModKey key) => new(0, key, null, null);
    public static ModProjectionRowKey Group(ModGridGroupKey key) => new(1, null, key, null);
    public static ModProjectionRowKey Retained(WorkshopId id, ModKey lastKnownKey) => new(2, lastKnownKey, null, id);
}

internal abstract record ModProjectionRowDefinition(ModProjectionRowKey Key);
internal sealed record GroupProjectionRow(ModGridGroupKey GroupKey, string Name, int Count, bool IsExpanded)
    : ModProjectionRowDefinition(ModProjectionRowKey.Group(GroupKey));
internal sealed record ModProjectionRow(ModProjectionEntry Entry, string State)
    : ModProjectionRowDefinition(ModProjectionRowKey.Mod(Entry.Installation.Key));
internal sealed record RetainedProjectionRow(RetainedWorkshopItem Item, string? WorkshopStatus)
    : ModProjectionRowDefinition(ModProjectionRowKey.Retained(Item.WorkshopId, Item.LastKnownKey));
