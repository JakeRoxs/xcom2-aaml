using AAML.Application.Common;
using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Mods;

namespace AAML.Application.Ports;

/// <summary>Loads and saves durable settings.</summary>
public interface ISettingsRepository
{
    Task<Result<ApplicationSettings>> LoadAsync(CancellationToken cancellationToken);
    Task<Result> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken);
}

/// <summary>Discovers physical mod installations without mutating them.</summary>
public interface IModCatalogSource
{
    Task<Result<IReadOnlyList<ModInstallation>>> DiscoverAsync(
        IReadOnlyList<string> roots,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Loads and saves game configuration snapshots.</summary>
public interface IGameConfigurationRepository
{
    Task<Result<GameConfigurationSnapshot>> LoadAsync(GameVariant variant, CancellationToken cancellationToken);
    Task<Result> SaveAsync(GameConfigurationSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Application-owned game configuration facts.</summary>
public sealed record GameConfigurationSnapshot(GameVariant Variant, IReadOnlyList<PackageId> ActiveMods, IReadOnlyList<string> ModRoots, string? Revision);
