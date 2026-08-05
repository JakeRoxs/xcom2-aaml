using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;

namespace AAML.Domain.Profiles;

public readonly record struct ProfileId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

/// <summary>A portable mod reference that intentionally excludes machine-specific paths.</summary>
public sealed record ProfileModEntry(ModSource Source, PackageId PackageId, WorkshopId? WorkshopId, int Order);

/// <summary>An immutable named snapshot of game mode, launch arguments, and ordered active mods.</summary>
public sealed record ModProfile(
    ProfileId Id,
    string Name,
    GameVariant GameVariant,
    IReadOnlyList<ProfileModEntry> Mods,
    IReadOnlyList<LaunchArgument> LaunchArguments,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    LegacyProfileMetadata? LegacyMetadata = null);

public sealed record LegacyProfileMetadata(string SourceFingerprint, IReadOnlyList<LegacyProfileRowMetadata> Rows);
public sealed record LegacyProfileRowMetadata(int Order, string? DisplayName, string? Category, IReadOnlyList<string> Tags, int SourceLine);
