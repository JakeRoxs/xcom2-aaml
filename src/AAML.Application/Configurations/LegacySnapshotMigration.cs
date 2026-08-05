using AAML.Application.Common;

namespace AAML.Application.Configurations;

public enum LegacySnapshotAction { Import, AlreadyImported, Conflict, Invalid }

public sealed record LegacySnapshotPreviewItem(
    int Index,
    string LegacyModName,
    string RawPath,
    SavedConfigurationSnapshot? Snapshot,
    LegacySnapshotAction Action,
    IReadOnlyList<string> Diagnostics);

public sealed record LegacySnapshotMigrationPreview(
    string SourcePath,
    string SourceFingerprint,
    IReadOnlyList<LegacySnapshotPreviewItem> Items,
    string Report);

public interface ILegacySnapshotMigrationService
{
    Task<Result<LegacySnapshotMigrationPreview>> PreviewAsync(string sourcePath, string contents, CancellationToken cancellationToken);
    Task<Result> ApplyAsync(LegacySnapshotMigrationPreview preview, CancellationToken cancellationToken);
}
