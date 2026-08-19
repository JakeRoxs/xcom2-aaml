using AAML.Domain.Games;
using AAML.Infrastructure.Linux.Paths;
using FluentAssertions;

namespace AAML.Infrastructure.Linux.Tests;

[TestClass]
public sealed class LinuxGameUserDataLocatorTests
{
    [TestMethod]
    public void UnsupportedVariantsAndMissingInstallationAreRejectedBeforePathDerivation()
    {
        var locator = new LinuxGameUserDataLocator();

        locator.Locate(GameVariant.XCom2WarOfTheChosenChallengeMode, "/tmp/fake").Error!.Code.Should().Be("game_data.variant_unsupported");
        locator.Locate(GameVariant.ChimeraSquad, "/tmp/fake").Error!.Code.Should().Be("game_data.variant_unsupported");
        locator.Locate(GameVariant.XCom2, null).Error!.Code.Should().Be("game_data.installation_required");
    }

    [TestMethod]
    public void QualifiedSteamLayout_ResolvesVanillaAndWotcWithoutCreatingGameData()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Exact Proton path behavior runs on Linux."); return; }
        var root = Path.Combine(Path.GetTempPath(), "aaml-game-data", Guid.NewGuid().ToString("N"));
        var steamApps = Path.Combine(root, "steamapps");
        var game = Path.Combine(steamApps, "common", "XCOM 2");
        var users = Path.Combine(steamApps, "compatdata", "268500", "pfx", "drive_c", "users");
        try
        {
            Directory.CreateDirectory(Path.Combine(game, "Binaries", "Win64"));
            Directory.CreateDirectory(Path.Combine(game, "XCom2-WarOfTheChosen", "Binaries", "Win64"));
            Directory.CreateDirectory(Path.Combine(users, "steamuser"));
            File.WriteAllText(Path.Combine(steamApps, "appmanifest_268500.acf"), "\"AppState\" { \"appid\" \"268500\" \"installdir\" \"XCOM 2\" }");
            File.WriteAllText(Path.Combine(game, "Binaries", "Win64", "XCom2.exe"), string.Empty);
            File.WriteAllText(Path.Combine(game, "XCom2-WarOfTheChosen", "Binaries", "Win64", "XCom2.exe"), string.Empty);
            var locator = new LinuxGameUserDataLocator();

            var vanilla = locator.Locate(GameVariant.XCom2, game);
            var wotc = locator.Locate(GameVariant.XCom2WarOfTheChosen, game);

            vanilla.Value!.UserDataDirectory.Should().Be(Path.Combine(users, "steamuser", "Documents", "My Games", "XCOM2"));
            vanilla.Value.ConfigurationDirectory.Should().Be(Path.Combine(vanilla.Value.UserDataDirectory, "XComGame", "Config"));
            wotc.Value!.UserDataDirectory.Should().Be(Path.Combine(users, "steamuser", "Documents", "My Games", "XCOM2 War of the Chosen"));
            wotc.Value.ConfigurationDirectory.Should().Be(Path.Combine(wotc.Value.UserDataDirectory, "XComGame", "Config"));
            Directory.Exists(vanilla.Value.UserDataDirectory).Should().BeFalse();
            Directory.Exists(wotc.Value.UserDataDirectory).Should().BeFalse();

            File.WriteAllText(Path.Combine(steamApps, "appmanifest_268500.acf"), "malformed");
            locator.Locate(GameVariant.XCom2, game).Error!.Code.Should().Be("launch.steam_manifest_invalid");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
