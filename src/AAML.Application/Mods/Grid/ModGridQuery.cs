using AAML.Domain.Mods;

namespace AAML.Application.Mods.Grid;

/// <summary>Complete logical query for the flattened mod grid.</summary>
public sealed record ModGridQuery(
    string SearchText,
    bool IncludeHidden,
    IReadOnlySet<ModGridSemanticState> StateFilters,
    ModGridSort Sort,
    ModGridGrouping Grouping)
{
    public static ModGridQuery Default { get; } = new(
        string.Empty,
        false,
        new HashSet<ModGridSemanticState>(),
        new ModGridSort(ModGridSortColumn.Name, SortDirection.Ascending),
        ModGridGrouping.None);
}

public sealed record ModGridSort(ModGridSortColumn Column, SortDirection Direction);
public enum ModGridSortColumn { Name, PackageId, Order, Category, State, DateAdded }
public enum SortDirection { Ascending, Descending }

/// <summary>Supported non-overlapping grouping modes.</summary>
public abstract record ModGridGrouping
{
    private ModGridGrouping() { }

    public static ModGridGrouping None { get; } = new NoGrouping();
    public static ModGridGrouping Category { get; } = new CategoryGrouping();
    public static ModGridGrouping ForState(ModGridSemanticState state) => new StateGrouping(state);
    public static ModGridGrouping ForTag(TagId tag) => new TagGrouping(tag);

    public sealed record NoGrouping : ModGridGrouping;
    public sealed record CategoryGrouping : ModGridGrouping;
    public sealed record StateGrouping(ModGridSemanticState State) : ModGridGrouping;
    public sealed record TagGrouping(TagId Tag) : ModGridGrouping;
}
