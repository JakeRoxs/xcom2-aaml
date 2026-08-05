namespace AAML.Domain.Games;

/// <summary>A valid launcher game mode.</summary>
public enum GameVariant
{
    XCom2,
    XCom2WarOfTheChosen,
    XCom2WarOfTheChosenChallengeMode,
    ChimeraSquad
}

/// <summary>Pure policy for supported game modes.</summary>
public static class GameVariantPolicy
{
    /// <summary>Gets the Steam application ID associated with a game mode.</summary>
    public static uint GetSteamAppId(GameVariant variant) => variant switch
    {
        GameVariant.XCom2 or GameVariant.XCom2WarOfTheChosen or GameVariant.XCom2WarOfTheChosenChallengeMode => 268500,
        GameVariant.ChimeraSquad => 882100,
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null)
    };

    /// <summary>Gets whether the mode permits mod activation.</summary>
    public static bool SupportsMods(GameVariant variant) => variant != GameVariant.XCom2WarOfTheChosenChallengeMode;
}
