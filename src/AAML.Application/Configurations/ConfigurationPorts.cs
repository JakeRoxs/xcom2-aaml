using AAML.Application.Common;
using AAML.Domain.Games;
using AAML.Domain.Mods;

namespace AAML.Application.Configurations;

public interface IConfigurationFileRepository
{
    Task<Result<ConfigurationFileVersion>> LoadAsync(ConfigurationDocumentId id, ConfigurationFileLimits limits, CancellationToken cancellationToken);
    Task<Result<SavedConfigurationSnapshot?>> LoadRecoveryAsync(ConfigurationDocumentId id, ConfigurationFileLimits limits, CancellationToken cancellationToken);
    Task<Result<ConfigurationSaveReceipt>> SaveAsync(ConfigurationDocumentId id, string text, ConfigurationTextFormat format, string expectedRevision, CancellationToken cancellationToken);
}

public interface IConfigurationSnapshotRepository
{
    Task<Result<SavedConfigurationSnapshot?>> FindAsync(ConfigurationDocumentId id, CancellationToken cancellationToken);
    Task<Result> UpsertAsync(SavedConfigurationSnapshot snapshot, CancellationToken cancellationToken);
    Task<Result> ImportAsync(IReadOnlyList<SavedConfigurationSnapshot> snapshots, CancellationToken cancellationToken);
    Task<Result> RemoveAsync(ConfigurationDocumentId id, CancellationToken cancellationToken);
}

public interface IConfigurationDocumentCatalog
{
    Task<Result<IReadOnlyList<ConfigurationDocumentSummary>>> ListAsync(IReadOnlyList<ModInstallation> installations, GameVariant variant, CancellationToken cancellationToken);
}
