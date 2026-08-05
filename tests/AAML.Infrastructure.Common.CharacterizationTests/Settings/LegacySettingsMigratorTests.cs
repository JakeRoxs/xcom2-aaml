using AAML.Application.Common;
using AAML.Domain.Mods;
using AAML.Domain.Games;
using AAML.Application.Startup;
using AAML.Application.Settings;
using AAML.Infrastructure.Common.Settings;
using FluentAssertions;

namespace AAML.Infrastructure.Common.CharacterizationTests.Settings;

[TestClass]
public sealed class LegacySettingsMigratorTests
{
    [TestMethod]
    public void CurrentXcomSettings_PreserveSupportedIntentAndDiscardUiState()
    {
        var json = CompatibilityFixture.Read("settings", "current-xcom2.json");

        var result = LegacySettingsMigrator.Migrate(json, (_, path) => Result<string>.Success(path));

        result.IsSuccess.Should().BeTrue();
        result.Value!.GameInstallationLocation.Should().Be("C:\\AML-Fixtures\\Games\\XCOM 2");
        result.Value.ModRootLocations.Should().HaveCount(2);
        result.Value.ModIntents.Should().ContainSingle();
        result.Value.ModIntents[0].Mod.Source.Should().Be(ModSource.Manual);
        result.Value.ModIntents[0].Note.Should().Be("Synthetic fixture note.");
        result.Value.ModIntents[0].IgnoredDependencies.Should().Contain(new WorkshopId(900000003));
        result.Value.ModIntents[0].IsActive.Should().BeTrue();
        result.Value.ModIntents[0].ExplicitOrder.Should().Be(0);
        result.Value.ModIntents[0].ManualName.Should().Be("Synthetic Mod");
        result.Value.Categories.Should().ContainSingle(category => category.Name == "Compatibility Fixtures");
        result.Value.Tags.Should().HaveCount(2);
    }

    [TestMethod]
    public void ChimeraSettings_PreserveGamePathRootsAndArguments()
    {
        var result = LegacySettingsMigrator.Migrate(CompatibilityFixture.Read("settings", "current-chimera.json"), (_, path) => Result<string>.Success(path));

        result.Value!.SelectedGame.Should().Be(GameVariant.ChimeraSquad);
        result.Value.GameInstallationLocation.Should().EndWith("XCOM Chimera Squad");
        result.Value.ModRootLocations.Should().ContainSingle().Which.Should().EndWith("882100\\");
        result.Value.LaunchArguments.Select(argument => argument.Value).Should().Equal("-allowconsole", "-log");
    }

    [TestMethod]
    public void HistoricalCategoryShapes_PreserveMembershipAndNormalizeSentinelOrder()
    {
        const string json = """
            {
              "Game": 268500,
              "Mods": { "Entries": { "Intermediate": [
                { "Path": "C:\\Mods\\A", "Source": 4, "Index": -1, "Tags": ["Used"] }
              ] } },
              "Tags": { "unused-key": { "Label": "Unused", "Color": "ignored" } }
            }
            """;

        var first = LegacySettingsMigrator.Migrate(json, (_, path) => Result<string>.Success(path));
        var second = LegacySettingsMigrator.Migrate(json, (_, path) => Result<string>.Success(path));

        first.Value!.ModIntents.Should().ContainSingle(intent => intent.Category == first.Value.Categories.Single().Id);
        first.Value.ModIntents.Single().ExplicitOrder.Should().BeNull();
        first.Value.Tags.Select(tag => tag.Name).Should().BeEquivalentTo("Unused", "Used");
        second.Value!.Categories.Single().Id.Should().Be(first.Value.Categories.Single().Id);
        second.Value.Tags.Select(tag => tag.Id).Should().BeEquivalentTo(first.Value.Tags.Select(tag => tag.Id));
    }

    [TestMethod]
    public void DirectHistoricalCategory_PreservesMembership()
    {
        const string json = """{ "Mods": { "Legacy Group": [{ "Path": "C:\\Mods\\A", "Source": 4 }] } }""";

        var result = LegacySettingsMigrator.Migrate(json, (_, path) => Result<string>.Success(path));

        result.Value!.Categories.Should().ContainSingle(category => category.Name == "Legacy Group");
        result.Value.ModIntents.Should().ContainSingle(intent => intent.Category == result.Value.Categories.Single().Id);
    }

    [TestMethod]
    public void GlobalLegacySettings_AreNotPartOfMigrationOutput()
    {
        var json = CompatibilityFixture.Read("settings", "global-opted-out.json");

        var result = LegacySettingsMigrator.Migrate(json, (_, path) => Result<string>.Success(path));

        result.IsSuccess.Should().BeTrue();
        typeof(AAML.Application.Settings.ApplicationSettings).GetProperties().Select(property => property.Name)
            .Should().NotContain(name => name.Contains("Sentry", StringComparison.OrdinalIgnoreCase) || name.Contains("Telemetry", StringComparison.OrdinalIgnoreCase) || name == "Guid" || name == "UserName");
    }

    [TestMethod]
    public async Task FilesystemBootstrap_MigratesOnceAndNeverModifiesLegacySource()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Legacy Settings", Guid.NewGuid().ToString("N"));
        var paths = new TestPaths(root);
        var legacyPath = Path.Combine(root, "legacy-settings.json");
        var legacy = CompatibilityFixture.Read("settings", "current-xcom2.json");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(legacyPath, legacy, TestContext.CancellationToken);
            var repository = new JsonSettingsRepository(paths);
            var importer = new LegacySettingsFileImporter([legacyPath], (_, path) => Result<string>.Success(path));

            var first = await new SettingsBootstrapper(repository, importer).InitializeAsync(TestContext.CancellationToken);
            var second = await new SettingsBootstrapper(repository, importer).InitializeAsync(TestContext.CancellationToken);

            first.Value!.Origin.Should().Be(SettingsOrigin.MigratedLegacy);
            second.Value!.Origin.Should().Be(SettingsOrigin.Existing);
            second.Value.Settings.Should().BeEquivalentTo(first.Value.Settings with
            {
                GameLocations = new Dictionary<GameVariant, GameLocationSettings>
                {
                    [first.Value.Settings.SelectedGame] = new(first.Value.Settings.GameInstallationLocation, first.Value.Settings.ModRootLocations)
                },
                DuplicatePreferences = [],
                ModGrid = ModGridPreferences.Default,
                RetainedWorkshopItems = []
            });
            (await File.ReadAllTextAsync(legacyPath, TestContext.CancellationToken)).Should().Be(legacy);
            var modern = await File.ReadAllTextAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), TestContext.CancellationToken);
            modern.Should().NotContain("Windows").And.NotContain("Collapsed").And.NotContain("PreviousState").And.NotContain("SteamTags").And.NotContain("Settings\"");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }

    private sealed class TestPaths(string root) : AAML.Application.Ports.IApplicationPaths
    {
        public string ConfigurationDirectory { get; } = Path.Combine(root, "Config");
        public string DataDirectory { get; } = Path.Combine(root, "Data");
        public string StateDirectory { get; } = Path.Combine(root, "State");
        public string CacheDirectory { get; } = Path.Combine(root, "Cache");
        public string RuntimeDirectory { get; } = Path.Combine(root, "Runtime");
    }
}
