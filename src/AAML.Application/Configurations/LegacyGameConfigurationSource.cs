using AAML.Application.Common;
using AAML.Domain.Games;

namespace AAML.Application.Configurations;

public sealed record ObsoleteOverridePreview(GameVariant Variant, string Path, string SourceFingerprint, int RemovedRows, string RevisedContents, string Report);

public interface ILegacyGameConfigurationSource
{
    Task<Result<IReadOnlyList<ActiveModSource>>> ReadActiveModsAsync(GameVariant variant, string? installationLocation, CancellationToken cancellationToken);
    Task<Result<ObsoleteOverridePreview>> PreviewOverrideCleanupAsync(GameVariant variant, CancellationToken cancellationToken);
    Task<Result> ApplyOverrideCleanupAsync(ObsoleteOverridePreview preview, CancellationToken cancellationToken);
}

public sealed class UnavailableLegacyGameConfigurationSource : ILegacyGameConfigurationSource
{
    private static Error Unsupported() => new("legacy_configuration.unsupported_platform", "Automatic .NET Framework AML configuration discovery is available on Windows only.", ErrorKind.Validation);
    public Task<Result<IReadOnlyList<ActiveModSource>>> ReadActiveModsAsync(GameVariant variant, string? installationLocation, CancellationToken cancellationToken) => Task.FromResult(Result<IReadOnlyList<ActiveModSource>>.Failure(Unsupported()));
    public Task<Result<ObsoleteOverridePreview>> PreviewOverrideCleanupAsync(GameVariant variant, CancellationToken cancellationToken) => Task.FromResult(Result<ObsoleteOverridePreview>.Failure(Unsupported()));
    public Task<Result> ApplyOverrideCleanupAsync(ObsoleteOverridePreview preview, CancellationToken cancellationToken) => Task.FromResult(Result.Failure(Unsupported()));
}
