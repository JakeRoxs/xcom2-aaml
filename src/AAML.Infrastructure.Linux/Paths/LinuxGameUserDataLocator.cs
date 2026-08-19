using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;
using AAML.Infrastructure.Linux.Launching;

namespace AAML.Infrastructure.Linux.Paths;

/// <summary>Resolves game data only from a qualified XCOM 2 Steam installation and its matching Proton prefix.</summary>
public sealed class LinuxGameUserDataLocator : IGameUserDataLocator
{
    public Result<GameUserDataLocations> Locate(GameVariant variant, string? installationLocation)
    {
        if (variant is not (GameVariant.XCom2 or GameVariant.XCom2WarOfTheChosen))
            return Result<GameUserDataLocations>.Failure(new Error("game_data.variant_unsupported", "Linux user-data location discovery supports XCOM 2 Vanilla and War of the Chosen only.", ErrorKind.Validation));
        if (string.IsNullOrWhiteSpace(installationLocation))
            return Result<GameUserDataLocations>.Failure(new Error("game_data.installation_required", "Configure the selected Steam game installation before opening its Proton user data.", ErrorKind.Validation));
        var layout = LinuxSteamGameLayout.Resolve(installationLocation, variant);
        return layout.IsSuccess
            ? Result<GameUserDataLocations>.Success(new(layout.Value!.UserDataDirectory, layout.Value.ConfigurationDirectory))
            : Result<GameUserDataLocations>.Failure(layout.Error!);
    }
}
