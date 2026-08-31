using AAML.Domain.Games;
using FluentAssertions;

namespace AAML.Domain.Tests;

[TestClass]
public sealed class GameVariantDisplayTests
{
    [TestMethod]
    public void DisplayNamesUseShortFormAndChallengeModeIsFlagged()
    {
        GameVariantDisplay.GetDisplayName(GameVariant.XCom2).Should().Be("XCOM 2");
        GameVariantDisplay.GetDisplayName(GameVariant.XCom2WarOfTheChosen).Should().Be("WotC");
        GameVariantDisplay.GetDisplayName(GameVariant.XCom2WarOfTheChosenChallengeMode).Should().Be("WotC");
        GameVariantDisplay.GetDisplayName(GameVariant.ChimeraSquad).Should().Be("Chimera Squad");

        GameVariantDisplay.IsChallengeMode(GameVariant.XCom2WarOfTheChosenChallengeMode).Should().BeTrue();
        GameVariantDisplay.IsChallengeMode(GameVariant.XCom2WarOfTheChosen).Should().BeFalse();
        GameVariantDisplay.IsChallengeMode(GameVariant.XCom2).Should().BeFalse();
        GameVariantDisplay.IsChallengeMode(GameVariant.ChimeraSquad).Should().BeFalse();
    }

    [TestMethod]
    public void SelectorNamesDistinguishChallengeModeFromStandardWotC()
    {
        GameVariantDisplay.GetSelectorDisplayName(GameVariant.XCom2).Should().Be("XCOM 2");
        GameVariantDisplay.GetSelectorDisplayName(GameVariant.XCom2WarOfTheChosen).Should().Be("WotC");
        GameVariantDisplay.GetSelectorDisplayName(GameVariant.XCom2WarOfTheChosenChallengeMode).Should().Be("WotC (Challenge)");
        GameVariantDisplay.GetSelectorDisplayName(GameVariant.ChimeraSquad).Should().Be("Chimera Squad");
    }
}
