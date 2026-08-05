namespace AAML.Domain.Games;

/// <summary>Defines the game-owned locations used when importing generated mod roots.</summary>
public static class GameModRootPolicy
{
    public static string WindowsDocumentsFolder(GameVariant variant) => variant switch
    {
        GameVariant.XCom2 => "XCOM2",
        GameVariant.XCom2WarOfTheChosen => "XCOM2 War of the Chosen",
        GameVariant.ChimeraSquad => "XCOM Chimera Squad",
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Challenge mode has no independent generated configuration.")
    };

    public static string[] BinaryDirectoryComponents(GameVariant variant) => variant switch
    {
        GameVariant.XCom2 or GameVariant.ChimeraSquad => ["Binaries", "Win64"],
        GameVariant.XCom2WarOfTheChosen => ["XCom2-WarOfTheChosen", "Binaries", "Win64"],
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Challenge mode has no independent binary directory.")
    };

    public static bool SupportsLinuxProton(GameVariant variant) =>
        variant is GameVariant.XCom2 or GameVariant.XCom2WarOfTheChosen;
}
