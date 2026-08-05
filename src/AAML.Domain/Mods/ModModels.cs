namespace AAML.Domain.Mods;

/// <summary>Physical facts discovered for one mod installation.</summary>
public sealed record ModInstallation(
    ModKey Key,
    PackageId PackageId,
    string Name,
    WorkshopId? WorkshopId,
    bool RequiresWarOfTheChosen,
    DescriptorState DescriptorState,
    DateTimeOffset? DateAdded,
    ModInstallationMetadata? Metadata = null);

public sealed record ModInstallationMetadata(string DescriptorPath, string? DescriptorCategory, string? Description, IReadOnlyList<string> DescriptorTags, string? PreviewImagePath, string? ReadmePath, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Whether the mod descriptor is currently enabled on disk.</summary>
public enum DescriptorState
{
    Enabled,
    Disabled
}

/// <summary>User-authored choices that must survive migration.</summary>
public sealed record ModUserIntent(
    ModKey Mod,
    bool IsActive,
    bool IsHidden,
    int? ExplicitOrder,
    string? ManualName,
    CategoryId? Category,
    IReadOnlySet<TagId> Tags,
    string? Note,
    IReadOnlySet<WorkshopId> IgnoredDependencies);

/// <summary>A category record that does not own mods.</summary>
public sealed record Category(CategoryId Id, string Name, int Order);

/// <summary>A tag record that does not own mods.</summary>
public sealed record Tag(TagId Id, string Name, string? Color = null);

/// <summary>Derived status recomputed from installation and external facts.</summary>
public sealed record ModStatus(
    InstallationStatus Installation,
    DuplicateStatus Duplicate,
    DependencyStatus Dependencies,
    ConflictStatus Conflicts,
    UpdateStatus Update);

public enum InstallationStatus { Installed, Missing, OutsideConfiguredRoots }
public enum DuplicateStatus { None, Unresolved, Preferred, Secondary }
public enum DependencyStatus { Satisfied, Missing, Unknown }
public enum ConflictStatus { None, Conflicting }
public enum UpdateStatus { Unknown, Current, Available, Downloading }
public sealed record DuplicatePreference(PackageId PackageId, ModKey PreferredInstallation);
public sealed record RetainedWorkshopItem(WorkshopId WorkshopId, PackageId PackageId, string Name, ModKey LastKnownKey);
