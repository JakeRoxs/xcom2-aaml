using AAML.Application.Diagnostics;
using AAML.Domain.Games;

namespace AAML.Infrastructure.Windows.Paths;

public sealed class WindowsGameLogLocator : IGameLogLocator
{
    public string? GetCurrentLogPath(GameVariant variant)
    {
        var folder = variant switch { GameVariant.XCom2 => "XCOM2", GameVariant.XCom2WarOfTheChosen or GameVariant.XCom2WarOfTheChosenChallengeMode => "XCOM2 War of the Chosen", GameVariant.ChimeraSquad => "XCOM Chimera Squad", _ => null };
        return folder is null ? null : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", folder, "XComGame", "Logs", "Launch.log");
    }
}
