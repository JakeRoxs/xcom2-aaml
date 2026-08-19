using AAML.Domain.Games;
using AAML.Infrastructure.Windows.Paths;
using FluentAssertions;

namespace AAML.Infrastructure.Windows.Tests;

[TestClass]
public sealed class WindowsGameUserDataLocatorTests
{
    [TestMethod]
    [DataRow(GameVariant.XCom2, "XCOM2")]
    [DataRow(GameVariant.XCom2WarOfTheChosen, "XCOM2 War of the Chosen")]
    [DataRow(GameVariant.XCom2WarOfTheChosenChallengeMode, "XCOM2 War of the Chosen")]
    [DataRow(GameVariant.ChimeraSquad, "XCOM Chimera Squad")]
    public void Locate_UsesExactRedirectedDocumentsHierarchy(GameVariant variant, string folder)
    {
        var documents = Path.Combine(Path.GetTempPath(), "Redirected Documents Ω", Guid.NewGuid().ToString("N"));

        var result = new WindowsGameUserDataLocator(documents).Locate(variant, null);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value!.UserDataDirectory.Should().Be(Path.GetFullPath(Path.Combine(documents, "My Games", folder)));
        result.Value.ConfigurationDirectory.Should().Be(Path.GetFullPath(Path.Combine(documents, "My Games", folder, "XComGame", "Config")));
        Directory.Exists(result.Value.UserDataDirectory).Should().BeFalse("location resolution must not create game-owned folders");
    }

    [TestMethod]
    public void Locate_ReportsUnavailableDocumentsRoot()
    {
        var result = new WindowsGameUserDataLocator(string.Empty).Locate(GameVariant.XCom2, null);

        result.Error!.Code.Should().Be("game_data.documents_unavailable");
        result.Error.Message.Should().Contain("Documents");
    }
}
