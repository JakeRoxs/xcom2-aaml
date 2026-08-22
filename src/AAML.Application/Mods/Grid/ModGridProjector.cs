using AAML.Domain.Mods;

namespace AAML.Application.Mods.Grid;

/// <summary>Filters, sorts, groups, and flattens immutable mod grid read models.</summary>
public static class ModGridProjector
{
    private const string MatchBucketValue = "match";
    private const string OtherBucketValue = "other";

    public static IReadOnlyList<ModGridRow> Project(
        IEnumerable<ModGridItem> source,
        ModGridLookups lookups,
        ModGridQuery query,
        IReadOnlySet<ModGridGroupKey>? collapsedGroups = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(lookups);
        ArgumentNullException.ThrowIfNull(query);

        collapsedGroups ??= new HashSet<ModGridGroupKey>();
        var categories = lookups.Categories.ToDictionary(category => category.Id);
        var tags = lookups.Tags.ToDictionary(tag => tag.Id);
        var filtered = source.Where(item => Matches(item, query, categories, tags)).ToArray();

        if (query.Grouping is ModGridGrouping.NoGrouping)
        {
            return filtered.Order(CreateComparer(query.Sort, categories))
                .Select(item => (ModGridRow)new ModGridModRow(item.Key, null, item, ModGridSemanticStatePolicy.Calculate(item)))
                .ToArray();
        }

        var groups = filtered.GroupBy(item => GetBucket(item, query.Grouping, categories, tags));
        var rows = new List<ModGridRow>();
        foreach (var group in OrderGroups(groups, query.Grouping))
        {
            var items = group.Order(CreateComparer(query.Sort, categories)).ToArray();
            var expanded = !collapsedGroups.Contains(group.Key.Key);
            rows.Add(new ModGridGroupRow(group.Key.Key, group.Key.Label, items.Length, expanded));
            if (expanded)
            {
                rows.AddRange(items.Select(item => new ModGridModRow(item.Key, group.Key.Key, item, ModGridSemanticStatePolicy.Calculate(item))));
            }
        }

        return rows;
    }

    /// <summary>Evaluates the filter portion of a grid query for one immutable item.</summary>
    public static bool Matches(ModGridItem item, ModGridQuery query, ModGridLookups lookups)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(lookups);
        return CreatePredicate(query, lookups)(item);
    }

    /// <summary>Creates the filter predicate used by the grid projection without rebuilding lookup dictionaries per item.</summary>
    public static Func<ModGridItem, bool> CreatePredicate(ModGridQuery query, ModGridLookups lookups)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(lookups);
        var categories = lookups.Categories.ToDictionary(category => category.Id);
        var tags = lookups.Tags.ToDictionary(tag => tag.Id);
        return item => Matches(item, query, categories, tags);
    }

    /// <summary>Creates the deterministic comparer used by the grid projection.</summary>
    public static IComparer<ModGridItem> CreateComparer(ModGridSort sort, ModGridLookups lookups)
    {
        ArgumentNullException.ThrowIfNull(sort);
        ArgumentNullException.ThrowIfNull(lookups);
        return CreateComparer(sort, lookups.Categories.ToDictionary(category => category.Id));
    }

    private static bool Matches(ModGridItem item, ModGridQuery query, IReadOnlyDictionary<CategoryId, Category> categories, IReadOnlyDictionary<TagId, Tag> tags) =>
        (query.IncludeHidden || !item.IsHidden) &&
        MatchesSearch(item, query.SearchText, categories, tags) &&
        (query.StateFilters.Count == 0 || query.StateFilters.Contains(ModGridSemanticStatePolicy.Calculate(item)));

    private static bool MatchesSearch(
        ModGridItem item,
        string search,
        IReadOnlyDictionary<CategoryId, Category> categories,
        IReadOnlyDictionary<TagId, Tag> tags)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var term = search.Trim();
        return Contains(item.DisplayName, term) ||
               Contains(item.PackageId.Value, term) ||
               (item.WorkshopId is { } workshop && workshop.Value.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase)) ||
               (item.CategoryId is { } categoryId && categories.TryGetValue(categoryId, out var category) && Contains(category.Name, term)) ||
               item.TagIds.Any(tagId => tags.TryGetValue(tagId, out var tag) && Contains(tag.Name, term));
    }

    private static bool Contains(string value, string term) => value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static IComparer<ModGridItem> CreateComparer(ModGridSort sort, IReadOnlyDictionary<CategoryId, Category> categories) =>
        Comparer<ModGridItem>.Create((left, right) =>
        {
            var primary = ComparePrimary(left, right, sort.Column, categories);
            if (primary != 0) return sort.Direction == SortDirection.Ascending ? primary : -primary;
            var name = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            if (name != 0) return name;
            var package = StringComparer.OrdinalIgnoreCase.Compare(left.PackageId.Value, right.PackageId.Value);
            if (package != 0) return package;
            var sourceComparison = left.Key.Source.CompareTo(right.Key.Source);
            return sourceComparison != 0 ? sourceComparison : StringComparer.Ordinal.Compare(left.Key.LocationIdentity, right.Key.LocationIdentity);
        });

    private static int ComparePrimary(
        ModGridItem left,
        ModGridItem right,
        ModGridSortColumn column,
        IReadOnlyDictionary<CategoryId, Category> categories) => column switch
    {
        ModGridSortColumn.Name => StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName),
        ModGridSortColumn.PackageId => StringComparer.OrdinalIgnoreCase.Compare(left.PackageId.Value, right.PackageId.Value),
        ModGridSortColumn.Order => CompareNullable(left.ExplicitOrder, right.ExplicitOrder),
        ModGridSortColumn.Category => StringComparer.OrdinalIgnoreCase.Compare(CategoryName(left.CategoryId, categories), CategoryName(right.CategoryId, categories)),
        ModGridSortColumn.State => ModGridSemanticStatePolicy.Calculate(left).CompareTo(ModGridSemanticStatePolicy.Calculate(right)),
        ModGridSortColumn.DateAdded => Nullable.Compare(left.DateAdded, right.DateAdded),
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, null)
    };

    private static int CompareNullable(int? left, int? right)
    {
        if (left.HasValue != right.HasValue) return left.HasValue ? -1 : 1;
        return Nullable.Compare(left, right);
    }

    private static string CategoryName(CategoryId? id, IReadOnlyDictionary<CategoryId, Category> categories) =>
        id is { } value && categories.TryGetValue(value, out var category) ? category.Name : "Uncategorized";

    private static GroupBucket GetBucket(
        ModGridItem item,
        ModGridGrouping grouping,
        IReadOnlyDictionary<CategoryId, Category> categories,
        IReadOnlyDictionary<TagId, Tag> tags) => grouping switch
    {
        ModGridGrouping.CategoryGrouping => CategoryBucket(item.CategoryId, categories),
        ModGridGrouping.StateGrouping state => ModGridSemanticStatePolicy.Calculate(item) == state.State
            ? new GroupBucket(new ModGridGroupKey($"state:{state.State}", MatchBucketValue), state.State.ToString(), 0, MatchBucketValue)
            : new GroupBucket(new ModGridGroupKey($"state:{state.State}", OtherBucketValue), "Other", 1, OtherBucketValue),
        ModGridGrouping.TagGrouping tag => item.TagIds.Contains(tag.Tag)
            ? new GroupBucket(new ModGridGroupKey($"tag:{tag.Tag.Value}", MatchBucketValue), tags.GetValueOrDefault(tag.Tag)?.Name ?? tag.Tag.Value, 0, MatchBucketValue)
            : new GroupBucket(new ModGridGroupKey($"tag:{tag.Tag.Value}", OtherBucketValue), "Without tag", 1, OtherBucketValue),
        _ => throw new ArgumentOutOfRangeException(nameof(grouping), grouping, null)
    };

    private static GroupBucket CategoryBucket(CategoryId? id, IReadOnlyDictionary<CategoryId, Category> categories)
    {
        if (id is { } categoryId && categories.TryGetValue(categoryId, out var category))
        {
            return new GroupBucket(new ModGridGroupKey("category", categoryId.Value), category.Name, category.Order, categoryId.Value);
        }

        return new GroupBucket(new ModGridGroupKey("category", "uncategorized"), "Uncategorized", int.MaxValue, "uncategorized");
    }

    private static IEnumerable<IGrouping<GroupBucket, ModGridItem>> OrderGroups(
        IEnumerable<IGrouping<GroupBucket, ModGridItem>> groups,
        ModGridGrouping grouping) => grouping is ModGridGrouping.CategoryGrouping
        ? groups.OrderBy(group => group.Key.Order).ThenBy(group => group.Key.Label, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.Key.StableIdentity, StringComparer.Ordinal)
        : groups.OrderBy(group => group.Key.Order).ThenBy(group => group.Key.StableIdentity, StringComparer.Ordinal);

    private sealed record GroupBucket(ModGridGroupKey Key, string Label, int Order, string StableIdentity);
}
