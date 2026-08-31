namespace AAML.Domain.Games;

/// <summary>Pure display policy for game modes.</summary>
public static class GameVariantDisplay
{
    /// <summary>Gets the compact display name shown in the shell rail.</summary>
    public static string GetDisplayName(GameVariant variant) => variant switch
    {
        GameVariant.XCom2 => "XCOM 2",
        GameVariant.XCom2WarOfTheChosen => "WotC",
        GameVariant.XCom2WarOfTheChosenChallengeMode => "WotC",
        GameVariant.ChimeraSquad => "Chimera Squad",
        _ => "Unknown"
    };

    /// <summary>Gets a distinguishable name for game-selection pickers.</summary>
    public static string GetSelectorDisplayName(GameVariant variant) => variant switch
    {
        GameVariant.XCom2 => "XCOM 2",
        GameVariant.XCom2WarOfTheChosen => "WotC",
        GameVariant.XCom2WarOfTheChosenChallengeMode => "WotC (Challenge)",
        GameVariant.ChimeraSquad => "Chimera Squad",
        _ => "Unknown"
    };

    /// <summary>Gets whether the mode is the WOTC Challenge variant.</summary>
    public static bool IsChallengeMode(GameVariant? variant) => variant == GameVariant.XCom2WarOfTheChosenChallengeMode;
}
