using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Infrastructure.Linux.Launching;

namespace AAML.Infrastructure.Linux.Paths;

/// <summary>Resolves game data from a qualified XCOM 2 installation and matching runtime layout.</summary>
public sealed class LinuxGameUserDataLocator : IGameUserDataLocator
{
    public Result<GameUserDataLocations> Locate(GameVariant variant, string? installationLocation)
    {
        if (variant is not (GameVariant.XCom2 or GameVariant.XCom2WarOfTheChosen))
            return Result<GameUserDataLocations>.Failure(new Error("game_data.variant_unsupported", "Linux user-data location discovery supports XCOM 2 Vanilla and War of the Chosen only.", ErrorKind.Validation));
        if (string.IsNullOrWhiteSpace(installationLocation))
            return Result<GameUserDataLocations>.Failure(new Error("game_data.installation_required", "Configure the selected Steam game installation before opening its user data.", ErrorKind.Validation));

        var layout = LinuxGameRuntimeLayout.Resolve(installationLocation, variant, GameRuntime.Auto);
        if (!layout.IsSuccess)
            return Result<GameUserDataLocations>.Failure(layout.Error!);

        var value = layout.Value!;
        var userDataDir = value.UserDataDirectory;
        var configDir = value.ConfigurationDirectory;

        // If in-installation user-data is empty, fall back to Feral VFS user-data tree
        if (!Directory.Exists(userDataDir) || !Directory.EnumerateFiles(userDataDir).Any())
        {
            var feral = ResolveFeralUserData(installationLocation, variant);
            if (feral.IsSuccess)
            {
                userDataDir = feral.Value!.UserDataPath;
                configDir = feral.Value!.ConfigPath;
            }
        }

        return Result<GameUserDataLocations>.Success(new(userDataDir, configDir));
    }

    private static Result<(string UserDataPath, string ConfigPath)> ResolveFeralUserData(string game, GameVariant variant)
    {
        var feralRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "feral-interactive");
        if (!Directory.Exists(feralRoot))
            return Result<(string, string)>.Failure(new Error("game_data.feral_missing", "Feral user-data tree not found.", ErrorKind.NotFound));

        var feralGameDir = variant == GameVariant.XCom2
            ? Path.Combine(feralRoot, "XCOM2")
            : Path.Combine(feralRoot, "XCOM 2 WotC");
        if (!Directory.Exists(feralGameDir))
            return Result<(string, string)>.Failure(new Error("game_data.feral_missing", $"Feral user-data directory not found: {feralGameDir}", ErrorKind.NotFound));

        var vfsLocal = Path.Combine(feralGameDir, "VFS", "Local");
        if (!Directory.Exists(vfsLocal))
            return Result<(string, string)>.Failure(new Error("game_data.feral_missing", $"Feral VFS Local not found: {vfsLocal}", ErrorKind.NotFound));

        var myGames = Path.Combine(vfsLocal, "my games");
        if (!Directory.Exists(myGames))
            return Result<(string, string)>.Failure(new Error("game_data.feral_missing", $"Feral my games not found: {myGames}", ErrorKind.NotFound));

        var xcomGameDir = variant == GameVariant.XCom2
            ? Path.Combine(myGames, "XCOM2", "XComGame")
            : Path.Combine(myGames, "XCOM2 War of the Chosen", "XComGame");
        if (!Directory.Exists(xcomGameDir))
            return Result<(string, string)>.Failure(new Error("game_data.feral_missing", $"Feral XComGame not found: {xcomGameDir}", ErrorKind.NotFound));

        var saveData = Path.Combine(xcomGameDir, "SaveData");
        var configDir = Path.Combine(xcomGameDir, "Config");

        var hasSaveData = Directory.Exists(saveData) && Directory.EnumerateFiles(saveData).Any();
        var hasConfig = Directory.Exists(configDir) && Directory.EnumerateFiles(configDir).Any();
        if (!hasSaveData && !hasConfig)
            return Result<(string, string)>.Failure(new Error("game_data.feral_empty", $"Feral user-data directory exists but is empty: {xcomGameDir}", ErrorKind.NotFound));

        return Result<(string, string)>.Success((xcomGameDir, configDir));
    }
}
