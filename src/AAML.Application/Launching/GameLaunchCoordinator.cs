using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Launching;

namespace AAML.Application.Launching;

public interface IGameLaunchCoordinator
{
    Task<Result<GameLaunchOutcome>> LaunchAsync(GameLaunchRequest request, CancellationToken cancellationToken);
}

public sealed record GameLaunchOutcome(GameConfigurationReceipt? Configuration, GameLaunchReceipt Launch);

/// <summary>Applies launch policy and configuration before delegating platform process startup.</summary>
public sealed class GameLaunchCoordinator(IGameConfigurationWriter configurationWriter, IGameLauncher launcher) : IGameLaunchCoordinator
{
    public async Task<Result<GameLaunchOutcome>> LaunchAsync(GameLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.GameInstallationLocation))
            return Result<GameLaunchOutcome>.Failure(new Error("launch.installation_required", "A game installation must be selected.", ErrorKind.Validation));
        var normalized = GameLaunchPolicy.Normalize(request);
        if (normalized.Variant == AAML.Domain.Games.GameVariant.XCom2 && normalized.ActiveMods.Any(mod => mod.RequiresWarOfTheChosen))
            return Result<GameLaunchOutcome>.Failure(new Error("launch.wotc_mods_in_vanilla", "One or more active mods require War of the Chosen. Disable them or select War of the Chosen before launching.", ErrorKind.Conflict));
        var validated = await launcher.ValidateAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (!validated.IsSuccess) return Result<GameLaunchOutcome>.Failure(validated.Error!);
        GameConfigurationReceipt? configuration = null;
        if (normalized.ApplyConfiguration)
        {
            var applied = await configurationWriter.ApplyAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (!applied.IsSuccess) return Result<GameLaunchOutcome>.Failure(applied.Error!);
            configuration = applied.Value;
        }
        var launched = await launcher.LaunchAsync(normalized, cancellationToken).ConfigureAwait(false);
        return launched.IsSuccess
            ? Result<GameLaunchOutcome>.Success(new GameLaunchOutcome(configuration, launched.Value!))
            : Result<GameLaunchOutcome>.Failure(launched.Error!);
    }
}
