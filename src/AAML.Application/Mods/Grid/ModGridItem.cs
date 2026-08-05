using AAML.Domain.Mods;

namespace AAML.Application.Mods.Grid;

/// <summary>Joined read model for one physical mod installation in the mod grid.</summary>
public sealed record ModGridItem(
    ModKey Key,
    PackageId PackageId,
    WorkshopId? WorkshopId,
    string DisplayName,
    bool IsActive,
    bool IsHidden,
    int? ExplicitOrder,
    CategoryId? CategoryId,
    IReadOnlySet<TagId> TagIds,
    ModStatus Status,
    bool RequiresWarOfTheChosen,
    DateTimeOffset? DateAdded);

/// <summary>Single semantic status shown for a mod row, ordered by explicit policy.</summary>
public enum ModGridSemanticState
{
    Ok,
    NotInstalled,
    OutsideConfiguredRoots,
    MissingDependencies,
    Conflict,
    Duplicate,
    UpdateAvailable
}

/// <summary>Calculates a deterministic display status without relying on enum numeric values.</summary>
public static class ModGridSemanticStatePolicy
{
    public static ModGridSemanticState Calculate(ModGridItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Status.Installation == InstallationStatus.Missing) return ModGridSemanticState.NotInstalled;
        if (item.Status.Installation == InstallationStatus.OutsideConfiguredRoots) return ModGridSemanticState.OutsideConfiguredRoots;
        if (item.IsActive && item.Status.Dependencies == DependencyStatus.Missing) return ModGridSemanticState.MissingDependencies;
        if (item.Status.Conflicts == ConflictStatus.Conflicting) return ModGridSemanticState.Conflict;
        if (item.Status.Duplicate != DuplicateStatus.None) return ModGridSemanticState.Duplicate;
        if (item.Status.Update == UpdateStatus.Available) return ModGridSemanticState.UpdateAvailable;
        return ModGridSemanticState.Ok;
    }
}
