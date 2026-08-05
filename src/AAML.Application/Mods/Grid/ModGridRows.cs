using AAML.Domain.Mods;

namespace AAML.Application.Mods.Grid;

/// <summary>Stable identity for one synthetic group row.</summary>
public readonly record struct ModGridGroupKey(string Grouping, string Bucket);

/// <summary>A flattened group or mod row consumed by a tabular presentation.</summary>
public abstract record ModGridRow;

public sealed record ModGridGroupRow(ModGridGroupKey Key, string Label, int ItemCount, bool IsExpanded) : ModGridRow;

public sealed record ModGridModRow(ModKey Key, ModGridGroupKey? Parent, ModGridItem Item, ModGridSemanticState SemanticState) : ModGridRow;

/// <summary>Lookup data needed to display stable category and tag identities.</summary>
public sealed record ModGridLookups(IReadOnlyList<Category> Categories, IReadOnlyList<Tag> Tags);
