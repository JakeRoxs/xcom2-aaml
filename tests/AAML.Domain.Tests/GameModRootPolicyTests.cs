using AAML.Domain.Games;
using FluentAssertions;

namespace AAML.Domain.Tests;

[TestClass]
public sealed class GameModRootPolicyTests
{
    [TestMethod]
    public void VariantsExposeExactWindowsBinaryAndGeneratedConfigurationRules()
    {
        GameModRootPolicy.WindowsDocumentsFolder(GameVariant.XCom2).Should().Be("XCOM2");
        GameModRootPolicy.WindowsDocumentsFolder(GameVariant.XCom2WarOfTheChosen).Should().Be("XCOM2 War of the Chosen");
        GameModRootPolicy.WindowsDocumentsFolder(GameVariant.ChimeraSquad).Should().Be("XCOM Chimera Squad");
        GameModRootPolicy.BinaryDirectoryComponents(GameVariant.XCom2).Should().Equal("Binaries", "Win64");
        GameModRootPolicy.BinaryDirectoryComponents(GameVariant.XCom2WarOfTheChosen).Should().Equal("XCom2-WarOfTheChosen", "Binaries", "Win64");
        GameModRootPolicy.BinaryDirectoryComponents(GameVariant.ChimeraSquad).Should().Equal("Binaries", "Win64");
    }

    [TestMethod]
    public void LinuxSupportIsExactAndDoesNotClaimChimera()
    {
        GameModRootPolicy.SupportsLinuxProton(GameVariant.XCom2).Should().BeTrue();
        GameModRootPolicy.SupportsLinuxProton(GameVariant.XCom2WarOfTheChosen).Should().BeTrue();
        GameModRootPolicy.SupportsLinuxProton(GameVariant.ChimeraSquad).Should().BeFalse();
    }
}
