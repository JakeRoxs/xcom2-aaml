using AAML.Domain.Games;
using AAML.Domain.Launching;
using FluentAssertions;

namespace AAML.Domain.Tests;

[TestClass]
public sealed class GameLaunchPolicyTests
{
    [TestMethod]
    public void ChallengeMode_RemovesConsoleOnlyAndPreservesCasing()
    {
        var request = new GameLaunchRequest(
            GameVariant.XCom2WarOfTheChosenChallengeMode,
            "C:\\Games\\XCOM 2",
            [],
            [],
            [new LaunchArgument("-ALLOWCONSOLE"), new LaunchArgument("-Name=Mixed Case")]);

        var result = GameLaunchPolicy.Normalize(request);

        result.Arguments.Select(argument => argument.Value).Should().Equal("-Name=Mixed Case");
        result.ApplyConfiguration.Should().BeFalse();
        GameVariantPolicy.SupportsMods(result.Variant).Should().BeFalse();
        GameVariantPolicy.GetSteamAppId(result.Variant).Should().Be(268500);
    }

    [TestMethod]
    public void ChimeraSquad_UsesDistinctSteamApplication()
    {
        GameVariantPolicy.GetSteamAppId(GameVariant.ChimeraSquad).Should().Be(882100);
    }
}
