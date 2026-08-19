using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;

namespace AAML.Infrastructure.Windows.Paths;

/// <summary>Resolves generated game data beneath the current redirected Documents folder.</summary>
public sealed class WindowsGameUserDataLocator : IGameUserDataLocator
{
    private readonly string documentsDirectory;

    public WindowsGameUserDataLocator() : this(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) { }
    internal WindowsGameUserDataLocator(string documentsDirectory) => this.documentsDirectory = documentsDirectory;

    public Result<GameUserDataLocations> Locate(GameVariant variant, string? installationLocation)
    {
        if (string.IsNullOrWhiteSpace(documentsDirectory))
            return Result<GameUserDataLocations>.Failure(new Error("game_data.documents_unavailable", "The Windows Documents folder could not be resolved.", ErrorKind.Unavailable));
        var folder = variant switch
        {
            GameVariant.XCom2 => "XCOM2",
            GameVariant.XCom2WarOfTheChosen or GameVariant.XCom2WarOfTheChosenChallengeMode => "XCOM2 War of the Chosen",
            GameVariant.ChimeraSquad => "XCOM Chimera Squad",
            _ => null
        };
        if (folder is null)
            return Result<GameUserDataLocations>.Failure(new Error("game_data.variant_unsupported", "The selected game variant has no Windows user-data location.", ErrorKind.Validation));
        try
        {
            var userData = Path.GetFullPath(Path.Combine(documentsDirectory, "My Games", folder));
            return Result<GameUserDataLocations>.Success(new(userData, Path.Combine(userData, "XComGame", "Config")));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result<GameUserDataLocations>.Failure(new Error("game_data.path_invalid", exception.Message, ErrorKind.Validation));
        }
    }
}
