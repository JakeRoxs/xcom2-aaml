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
        var settings = result.Value!.Settings;
        settings.GameInstallationLocation.Should().Be("C:\\AML-Fixtures\\Games\\XCOM 2");
        settings.ModRootLocations.Should().HaveCount(2);
        settings.ModIntents.Should().ContainSingle();
        settings.ModIntents[0].Mod.Source.Should().Be(ModSource.Manual);
        settings.ModIntents[0].Note.Should().Be("Synthetic fixture note.");
        settings.ModIntents[0].IgnoredDependencies.Should().Contain(new WorkshopId(900000003));
        settings.ModIntents[0].IsActive.Should().BeTrue();
        settings.ModIntents[0].ExplicitOrder.Should().Be(0);
        settings.ModIntents[0].ManualName.Should().Be("Synthetic Mod");
        settings.Categories.Should().ContainSingle(category => category.Name == "Compatibility Fixtures");
        settings.Tags.Should().HaveCount(2);
    }

    [TestMethod]
    public void ChimeraSettings_PreserveGamePathRootsAndArguments()
    {
        var result = LegacySettingsMigrator.Migrate(CompatibilityFixture.Read("settings", "current-chimera.json"), (_, path) => Result<string>.Success(path));

        result.Value!.Settings.SelectedGame.Should().Be(GameVariant.ChimeraSquad);
        result.Value.Settings.GameInstallationLocation.Should().EndWith("XCOM Chimera Squad");
        result.Value.Settings.ModRootLocations.Should().ContainSingle().Which.Should().EndWith("882100\\");
        result.Value.Settings.LaunchArguments.Select(argument => argument.Value).Should().Equal("-allowconsole", "-log");
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

        first.Value!.Settings.ModIntents.Should().ContainSingle(intent => intent.Category == first.Value.Settings.Categories.Single().Id);
        first.Value.Settings.ModIntents.Single().ExplicitOrder.Should().BeNull();
        first.Value.Settings.Tags.Select(tag => tag.Name).Should().BeEquivalentTo("Unused", "Used");
        second.Value!.Settings.Categories.Single().Id.Should().Be(first.Value.Settings.Categories.Single().Id);
        second.Value.Settings.Tags.Select(tag => tag.Id).Should().BeEquivalentTo(first.Value.Settings.Tags.Select(tag => tag.Id));
    }

    [TestMethod]
    public void DirectHistoricalCategory_PreservesMembership()
    {
        const string json = """{ "Mods": { "Legacy Group": [{ "Path": "C:\\Mods\\A", "Source": 4 }] } }""";

        var result = LegacySettingsMigrator.Migrate(json, (_, path) => Result<string>.Success(path));

        result.Value!.Settings.Categories.Should().ContainSingle(category => category.Name == "Legacy Group");
        result.Value.Settings.ModIntents.Should().ContainSingle(intent => intent.Category == result.Value.Settings.Categories.Single().Id);
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
    public void PreferenceFixture_PreservesRepresentableValuesAndQuickToggleMetadata()
    {
        var result = LegacySettingsMigrator.Migrate(CompatibilityFixture.Read("settings", "legacy-preferences.json"), (_, path) => Result<string>.Success(path));

        result.IsSuccess.Should().BeTrue();
        var settings = result.Value!.Settings;
        settings.Theme.Should().Be(ThemePreference.Dark);
        settings.CheckForUpdates.Should().BeFalse();
        settings.UpdateChannel.Should().Be(UpdateChannelPreference.Stable);
        settings.CloseAfterLaunch.Should().BeTrue();
        settings.AllowMultipleInstances.Should().BeTrue();
        settings.WorkshopStartupRefresh.Should().Be(WorkshopStartupRefreshPolicy.ActiveMods);
        settings.ModGrid.Should().BeEquivalentTo(new ModGridPreferences(true, null, false, new HashSet<AAML.Application.Mods.Grid.ModGridGroupKey>()));
        settings.LaunchArguments.Select(argument => argument.Value).Should().Equal("-review", "-allowConsole");
        result.Value.Report.SchemaVersion.Should().Be(1);
        result.Value.Report.SourceSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        result.Value.Report.SourcePreserved.Should().BeTrue();
        result.Value.Report.QuickToggleArguments.Should().Equal("-allowConsole", "-log");
        result.Value.Report.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "legacy_settings.dormant_alpha_preference");
    }

    [TestMethod]
    public void TagColorFixture_NormalizesExactNewtonsoftShapesAndDiagnosesItems()
    {
        var first = LegacySettingsMigrator.Migrate(CompatibilityFixture.Read("settings", "legacy-tag-colors.json"), (_, path) => Result<string>.Success(path));
        var second = LegacySettingsMigrator.Migrate(CompatibilityFixture.Read("settings", "legacy-tag-colors.json"), (_, path) => Result<string>.Success(path));

        first.IsSuccess.Should().BeTrue();
        first.Value!.Settings.Tags.Single(tag => tag.Name == "Named").Color.Should().Be("#FF0000");
        first.Value.Settings.Tags.Single(tag => tag.Name == "RGB").Color.Should().Be("#112233");
        first.Value.Settings.Tags.Single(tag => tag.Name == "ARGB").Color.Should().Be("#11223380");
        first.Value.Settings.Tags.Where(tag => tag.Name == "Invalid" || tag.Name == "Wrong Type").Select(tag => tag.Color).Should().OnlyContain(color => color == null);
        first.Value.Report.Diagnostics.Should().HaveCount(2).And.OnlyContain(item => item.Code == "legacy_settings.invalid_tag_color");
        second.Value!.Settings.Tags.Select(tag => tag.Id).Should().Equal(first.Value.Settings.Tags.Select(tag => tag.Id));
    }

    [TestMethod]
    [DataRow("{}", ThemePreference.Light, true, UpdateChannelPreference.Stable, WorkshopStartupRefreshPolicy.AllMods, false, true)]
    [DataRow("{\"DarkMode\":false,\"CheckForUpdates\":false,\"CheckForPreReleaseUpdates\":true,\"UpdateModsOnStartup\":false,\"ShowHiddenElements\":true,\"ShowModListGroups\":false}", ThemePreference.Light, false, UpdateChannelPreference.Prerelease, WorkshopStartupRefreshPolicy.Manual, true, false)]
    [DataRow("{\"CheckForPreReleaseUpdates\":false,\"IncludeAlphaVersions\":true,\"UpdateModsOnStartup\":true,\"OnlyUpdateEnabledOrNewModsOnStartup\":true}", ThemePreference.Light, true, UpdateChannelPreference.Stable, WorkshopStartupRefreshPolicy.ActiveMods, false, true)]
    [DataRow("{\"CheckForPreReleaseUpdates\":true,\"IncludeAlphaVersions\":true,\"UpdateModsOnStartup\":false,\"OnlyUpdateEnabledOrNewModsOnStartup\":true}", ThemePreference.Light, true, UpdateChannelPreference.Alpha, WorkshopStartupRefreshPolicy.Manual, false, true)]
    public void PreferenceMappings_ApplyDefaultsAndPrecedence(string json, ThemePreference theme, bool updates, UpdateChannelPreference channel, WorkshopStartupRefreshPolicy workshop, bool hidden, bool groups)
    {
        var settings = LegacySettingsMigrator.Migrate(json, (_, path) => Result<string>.Success(path)).Value!.Settings;

        settings.Theme.Should().Be(theme);
        settings.CheckForUpdates.Should().Be(updates);
        settings.UpdateChannel.Should().Be(channel);
        settings.WorkshopStartupRefresh.Should().Be(workshop);
        settings.ModGrid!.IncludeHidden.Should().Be(hidden);
        settings.ModGrid.GroupByCategory.Should().Be(groups);
    }

    [TestMethod]
    public void NullAndWrongTypePreferences_UseLegacyDefaultsAndProduceDiagnostics()
    {
        const string json = """{"DarkMode":null,"CheckForUpdates":"false","CheckForPreReleaseUpdates":1,"IncludeAlphaVersions":{},"CloseAfterLaunch":[],"AllowMultipleInstances":0,"UpdateModsOnStartup":null,"OnlyUpdateEnabledOrNewModsOnStartup":"true","ShowHiddenElements":null,"ShowModListGroups":42,"QuickToggleArguments":null}""";

        var result = LegacySettingsMigrator.Migrate(json, (_, path) => Result<string>.Success(path)).Value!;

        result.Settings.Theme.Should().Be(ThemePreference.Light);
        result.Settings.CheckForUpdates.Should().BeTrue();
        result.Settings.UpdateChannel.Should().Be(UpdateChannelPreference.Stable);
        result.Settings.CloseAfterLaunch.Should().BeFalse();
        result.Settings.AllowMultipleInstances.Should().BeFalse();
        result.Settings.WorkshopStartupRefresh.Should().Be(WorkshopStartupRefreshPolicy.AllMods);
        result.Settings.ModGrid.Should().BeEquivalentTo(new ModGridPreferences(false, null, true, new HashSet<AAML.Application.Mods.Grid.ModGridGroupKey>()));
        result.Report.QuickToggleArguments.Should().BeEmpty();
        result.Report.Diagnostics.Select(item => item.Path).Should().BeEquivalentTo(
            "DarkMode", "CheckForUpdates", "CheckForPreReleaseUpdates", "IncludeAlphaVersions", "CloseAfterLaunch",
            "AllowMultipleInstances", "UpdateModsOnStartup", "OnlyUpdateEnabledOrNewModsOnStartup", "ShowHiddenElements",
            "ShowModListGroups", "QuickToggleArguments");
    }

    [TestMethod]
    public void AbsentQuickToggleArguments_RetainLegacyConstructorDefaultsWithoutChangingLaunchArguments()
    {
        var result = LegacySettingsMigrator.Migrate("{}", (_, path) => Result<string>.Success(path)).Value!;

        result.Report.QuickToggleArguments.Should().Equal("-review", "-noRedScreens", "-noStartUpMovies", "-allowConsole", "-regenerateinis");
        result.Settings.LaunchArguments.Select(argument => argument.Value).Should().Equal("-review", "-noRedScreens").And.OnlyHaveUniqueItems();
        result.Report.Diagnostics.Should().BeEmpty();
    }

    [TestMethod]
    public void MissingNullAndMalformedTagColors_DoNotFailMigration()
    {
        const string json = """{"Tags":{"missing":{"Label":"Missing"},"null":{"Label":"Null","Color":null},"transparent":{"Label":"Transparent","Color":"128, 0, 255, 16"},"overflow":{"Label":"Overflow","Color":"256, 0, 0"},"empty":{"Label":"Empty","Color":"Empty"}}}""";

        var result = LegacySettingsMigrator.Migrate(json, (_, path) => Result<string>.Success(path)).Value!;

        result.Settings.Tags.Single(tag => tag.Name == "Missing").Color.Should().BeNull();
        result.Settings.Tags.Single(tag => tag.Name == "Null").Color.Should().BeNull();
        result.Settings.Tags.Single(tag => tag.Name == "Transparent").Color.Should().Be("#00FF1080");
        result.Settings.Tags.Single(tag => tag.Name == "Overflow").Color.Should().BeNull();
        result.Settings.Tags.Single(tag => tag.Name == "Empty").Color.Should().BeNull();
        result.Report.Diagnostics.Select(item => item.Path).Should().BeEquivalentTo("Tags.null.Color", "Tags.overflow.Color", "Tags.empty.Color");
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
            var reportPath = Path.Combine(paths.ConfigurationDirectory, "legacy-migration-v1.json");
            var importer = new LegacySettingsFileImporter([legacyPath], (_, path) => Result<string>.Success(path), reportPath);

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
                RetainedWorkshopItems = []
            });
            (await File.ReadAllTextAsync(legacyPath, TestContext.CancellationToken)).Should().Be(legacy);
            var modern = await File.ReadAllTextAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), TestContext.CancellationToken);
            modern.Should().NotContain("Windows").And.NotContain("Collapsed").And.NotContain("PreviousState").And.NotContain("SteamTags").And.NotContain("Settings\"");
            modern.Should().Contain("\"schemaVersion\": 9").And.Contain("\"theme\": \"Light\"").And.Contain("\"workshopStartupRefresh\": \"AllMods\"");
            File.Exists(reportPath).Should().BeTrue();
            var report = await File.ReadAllTextAsync(reportPath, TestContext.CancellationToken);
            report.Should().Contain("\"schemaVersion\": 1").And.Contain("\"sourcePreserved\": true").And.Contain("\"quickToggleArguments\"");
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
