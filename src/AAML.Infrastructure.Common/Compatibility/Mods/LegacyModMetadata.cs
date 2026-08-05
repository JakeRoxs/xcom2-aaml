namespace AAML.Infrastructure.Common.Compatibility.Mods;

/// <summary>Metadata read from a legacy XComMod descriptor.</summary>
public sealed record LegacyModMetadata(
    long PublishedFileId,
    string? Title,
    string Category,
    string Description,
    string Tags,
    bool RequiresExpansion,
    string ContentImage);
