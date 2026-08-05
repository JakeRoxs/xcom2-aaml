using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Settings;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using System.Text;

namespace AAML.Infrastructure.Common.CharacterizationTests.Settings;

[TestClass]
public sealed class JsonSettingsRepositoryTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    public async Task EverySchemaFixture_LoadsAsCanonicalSchemaNine(int schema)
    {
        var root = TemporaryRoot();
        var paths = new TestPaths(root);
        try
        {
            var path = await SeedFixtureAsync(paths, schema);
            var repository = new JsonSettingsRepository(paths);

            var loaded = await repository.LoadAsync(TestContext.CancellationToken);

            loaded.IsSuccess.Should().BeTrue(loaded.Error?.Message);
            loaded.Value!.SchemaVersion.Should().Be(9);
            loaded.Value.ModRootLocations.Should().NotBeNull();
            loaded.Value.LaunchArguments.Should().NotBeNull();
            loaded.Value.ModIntents.Should().NotBeNull();
            loaded.Value.Categories.Should().NotBeNull();
            loaded.Value.Tags.Should().NotBeNull();
            loaded.Value.GameLocations.Should().NotBeNull();
            loaded.Value.DuplicatePreferences.Should().NotBeNull();
            loaded.Value.ModGrid.Should().NotBeNull();
            loaded.Value.RetainedWorkshopItems.Should().NotBeNull();
            loaded.Value.LocationFor(loaded.Value.SelectedGame).Should().BeEquivalentTo(new GameLocationSettings(loaded.Value.GameInstallationLocation, loaded.Value.ModRootLocations));
            loaded.Value.NavigationRailMode.Should().Be(schema is 8 or 9 ? NavigationRailMode.Compact : NavigationRailMode.Expanded);
            loaded.Value.AutoSaveChanges.Should().Be(schema == 9);
            repository.LastLoadReport.Should().Be(new SettingsLoadReport(schema, schema < 9, schema < 9));
            JObject.Parse(await File.ReadAllTextAsync(path, TestContext.CancellationToken)).Value<int>("schemaVersion").Should().Be(9);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task VersionOneMigration_PreservesEverySupportedFieldAndAppliesOnlyDocumentedDefault()
    {
        var root = TemporaryRoot();
        var paths = new TestPaths(root);
        try
        {
            await SeedFixtureAsync(paths, 1);

            var loaded = (await new JsonSettingsRepository(paths).LoadAsync(TestContext.CancellationToken)).Value!;

            loaded.GameInstallationLocation.Should().Be("C:\\Games\\XCOM2");
            loaded.ModRootLocations.Should().Equal("C:\\Mods");
            loaded.LaunchArguments.Select(item => item.Value).Should().Equal("-review", "-noRedScreens");
            loaded.ModIntents.Should().ContainSingle();
            var intent = loaded.ModIntents.Single();
            intent.Mod.Source.Should().Be(ModSource.Manual);
            intent.Mod.LocationIdentity.Should().Be("C:\\Mods\\Alpha");
            intent.IsActive.Should().BeTrue();
            intent.IsHidden.Should().BeTrue();
            intent.ExplicitOrder.Should().Be(3);
            intent.ManualName.Should().Be("Alpha Custom");
            intent.Category!.Value.Value.Should().Be("strategy");
            intent.Tags.Select(tag => tag.Value).Should().Equal("favorite");
            intent.Note.Should().Be("keep");
            intent.IgnoredDependencies.Select(id => id.Value).Should().Equal(123UL);
            loaded.Categories.Single().Should().Be(new Category(new CategoryId("strategy"), "Strategy", 2));
            loaded.Tags.Single().Should().Be(new Tag(new TagId("favorite"), "Favorite", "#123456"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task OlderScalarLocation_WinsDeterministicallyWhileOtherGameLocationsSurvive()
    {
        var root = TemporaryRoot();
        var paths = new TestPaths(root);
        try
        {
            await SeedFixtureAsync(paths, 3);

            var loaded = (await new JsonSettingsRepository(paths).LoadAsync(TestContext.CancellationToken)).Value!;

            loaded.LocationFor(GameVariant.XCom2).Should().BeEquivalentTo(new GameLocationSettings("C:\\Games\\ScalarWins", ["C:\\ScalarMods"]));
            loaded.LocationFor(GameVariant.ChimeraSquad).InstallationLocation.Should().Be("I:\\Chimera");
            loaded.WorkshopStartupRefresh.Should().Be(WorkshopStartupRefreshPolicy.Manual);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PartialLegacyModGrid_DefaultsMembersIndependently()
    {
        var root = TemporaryRoot();
        var paths = new TestPaths(root);
        try
        {
            await SeedFixtureAsync(paths, 6);

            var grid = (await new JsonSettingsRepository(paths).LoadAsync(TestContext.CancellationToken)).Value!.ModGrid!;

            grid.IncludeHidden.Should().BeTrue();
            grid.StateFilter.Should().Be(AAML.Application.Mods.Grid.ModGridSemanticState.Duplicate);
            grid.GroupByCategory.Should().BeTrue();
            grid.CollapsedGroups.Should().BeEmpty();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(null, WorkshopStartupRefreshPolicy.AllMods)]
    [DataRow("Manual", WorkshopStartupRefreshPolicy.Manual)]
    [DataRow("Never", WorkshopStartupRefreshPolicy.Manual)]
    [DataRow("AllMods", WorkshopStartupRefreshPolicy.AllMods)]
    [DataRow("ActiveMods", WorkshopStartupRefreshPolicy.ActiveMods)]
    public async Task LegacyWorkshopValues_HaveExplicitCompatibilityMapping(string? wireValue, WorkshopStartupRefreshPolicy expected)
    {
        var root = TemporaryRoot();
        var paths = new TestPaths(root);
        try
        {
            var path = await SeedFixtureAsync(paths, 6);
            var json = JObject.Parse(await File.ReadAllTextAsync(path, TestContext.CancellationToken));
            if (wireValue is null) json.Property("workshopStartupRefresh")!.Remove();
            else json["workshopStartupRefresh"] = wireValue;
            await File.WriteAllTextAsync(path, json.ToString(), TestContext.CancellationToken);

            var loaded = await new JsonSettingsRepository(paths).LoadAsync(TestContext.CancellationToken);

            loaded.Value!.WorkshopStartupRefresh.Should().Be(expected);
            JObject.Parse(await File.ReadAllTextAsync(path, TestContext.CancellationToken)).Value<string>("workshopStartupRefresh").Should().Be(expected.ToString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("Manual", WorkshopStartupRefreshPolicy.Manual)]
    [DataRow("AllMods", WorkshopStartupRefreshPolicy.AllMods)]
    [DataRow("ActiveMods", WorkshopStartupRefreshPolicy.ActiveMods)]
    public async Task CurrentWorkshopValues_AcceptExactDefinedNames(string wireValue, WorkshopStartupRefreshPolicy expected)
    {
        var root = TemporaryRoot();
        var paths = new TestPaths(root);
        try
        {
            var path = await SeedFixtureAsync(paths, 8);
            var json = JObject.Parse(await File.ReadAllTextAsync(path, TestContext.CancellationToken));
            json["workshopStartupRefresh"] = wireValue;
            await File.WriteAllTextAsync(path, json.ToString(), TestContext.CancellationToken);

            var loaded = await new JsonSettingsRepository(paths).LoadAsync(TestContext.CancellationToken);

            loaded.IsSuccess.Should().BeTrue(loaded.Error?.Message);
            loaded.Value!.WorkshopStartupRefresh.Should().Be(expected);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow("Never")]
    [DataRow("manual")]
    [DataRow("Sometimes")]
    [DataRow("0")]
    public async Task CurrentWorkshopValues_RejectLegacyUnknownWrongCaseAndNumericNames(string wireValue)
    {
        await AssertFixtureMutationInvalidAsync(8, json => json["workshopStartupRefresh"] = wireValue);
    }

    [TestMethod]
    [DataRow("Sometimes")]
    [DataRow("manual")]
    [DataRow("0")]
    [DataRow("1")]
    [DataRow("99")]
    public async Task LegacyWorkshopValues_RejectUnknownWrongCaseAndNumericEncodings(string wireValue)
    {
        await AssertFixtureMutationInvalidAsync(6, json => json["workshopStartupRefresh"] = wireValue);
    }

    [TestMethod]
    public async Task PersistedEnums_RejectJsonNumberTokens()
    {
        await AssertFixtureMutationInvalidAsync(6, json => json["workshopStartupRefresh"] = 1);
        await AssertFixtureMutationInvalidAsync(8, json => json["workshopStartupRefresh"] = 0);
        await AssertFixtureMutationInvalidAsync(8, json => json["selectedGame"] = 0);
        await AssertFixtureMutationInvalidAsync(8, json => json["theme"] = 2);
        await AssertFixtureMutationInvalidAsync(8, json => json["updateChannel"] = 0);
        await AssertFixtureMutationInvalidAsync(8, json => json["navigationRailMode"] = 0);
        await AssertFixtureMutationInvalidAsync(8, json => json["modGrid"]!["stateFilter"] = 5);
    }

    [TestMethod]
    [DataRow("selectedGame", "1")]
    [DataRow("theme", "1")]
    [DataRow("updateChannel", "1")]
    [DataRow("theme", "dark")]
    [DataRow("updateChannel", "stable")]
    public async Task PersistedTopLevelEnums_RejectNumericUndefinedAndWrongCase(string property, string value)
    {
        await AssertFixtureMutationInvalidAsync(8, json => json[property] = value);
    }

    [TestMethod]
    public async Task PersistedNestedEnums_RejectNumericAndUnknownNames()
    {
        await AssertFixtureMutationInvalidAsync(8, json => json["modGrid"]!["stateFilter"] = "99");
        await AssertFixtureMutationInvalidAsync(1, json => json["modIntents"]![0]!["source"] = "NotASource");
        await AssertFixtureMutationInvalidAsync(1, json => json["modIntents"]![0]!["source"] = 1);
    }

    [TestMethod]
    public async Task UnknownFutureSchemaAndContradictoryCurrentLocations_FailClosed()
    {
        await AssertFixtureMutationInvalidAsync(9, json => json["schemaVersion"] = 10);
        await AssertFixtureMutationInvalidAsync(9, json => json["gameLocations"]![0]!["installationLocation"] = "C:\\Contradiction");
        await AssertFixtureMutationInvalidAsync(9, json => json["modGrid"]!["includeHidden"]!.Parent!.Remove());
    }

    [TestMethod]
    public async Task Save_RejectsUndefinedCurrentDomainEnums()
    {
        var root = TemporaryRoot();
        try
        {
            var settings = Settings("C:\\Games\\XCOM 2") with { WorkshopStartupRefresh = (WorkshopStartupRefreshPolicy)99 };

            var saved = await new JsonSettingsRepository(new TestPaths(root)).SaveAsync(settings, TestContext.CancellationToken);

            saved.IsSuccess.Should().BeFalse();
            saved.Error!.Code.Should().Be("settings.invalid");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Save_RejectsUndefinedNavigationRailMode()
    {
        var root = TemporaryRoot();
        try
        {
            var settings = Settings("C:\\Games\\XCOM 2") with { NavigationRailMode = (NavigationRailMode)99 };

            var saved = await new JsonSettingsRepository(new TestPaths(root)).SaveAsync(settings, TestContext.CancellationToken);

            saved.IsSuccess.Should().BeFalse();
            saved.Error!.Code.Should().Be("settings.invalid");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("expanded")]
    [DataRow("0")]
    [DataRow("Wide")]
    public async Task SchemaEightNavigationRailMode_RequiresExactDefinedName(string? wireValue)
    {
        await AssertFixtureMutationInvalidAsync(8, json =>
        {
            if (wireValue is null) json.Property("navigationRailMode")!.Remove();
            else json["navigationRailMode"] = wireValue;
        });
    }

    [TestMethod]
    [DataRow("Expanded", NavigationRailMode.Expanded)]
    [DataRow("Compact", NavigationRailMode.Compact)]
    public async Task SchemaEightNavigationRailMode_AcceptsExactDefinedNames(string wireValue, NavigationRailMode expected)
    {
        var root = TemporaryRoot();
        var paths = new TestPaths(root);
        try
        {
            var path = await SeedFixtureAsync(paths, 8);
            var json = JObject.Parse(await File.ReadAllTextAsync(path, TestContext.CancellationToken));
            json["navigationRailMode"] = wireValue;
            await File.WriteAllTextAsync(path, json.ToString(), TestContext.CancellationToken);

            var loaded = await new JsonSettingsRepository(paths).LoadAsync(TestContext.CancellationToken);

            loaded.IsSuccess.Should().BeTrue(loaded.Error?.Message);
            loaded.Value!.NavigationRailMode.Should().Be(expected);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SchemaNineAutoSaveChanges_IsRequiredAndMustBeBooleanAndTemporaryAliasIsRejected()
    {
        await AssertFixtureMutationInvalidAsync(9, json => json.Property("autoSaveChanges")!.Remove());
        await AssertFixtureMutationInvalidAsync(9, json => json["autoSaveChanges"] = null);
        await AssertFixtureMutationInvalidAsync(9, json => json["autoSaveChanges"] = "false");
        await AssertFixtureMutationInvalidAsync(9, json => json["autoSaveChanges"] = 0);
        await AssertFixtureMutationInvalidAsync(9, json => json.Property("autoSaveChanges")!.Replace(new JProperty("autoSaveModChanges", false)));
    }

    [TestMethod]
    public async Task SchemaNineCanonicalContract_RejectsMissingScalarAndUnknownMembers()
    {
        await AssertFixtureMutationInvalidAsync(9, json => json.Property("closeAfterLaunch")!.Remove());
        await AssertFixtureMutationInvalidAsync(9, json => json["unexpected"] = true);
        await AssertFixtureMutationInvalidAsync(9, json => json["modGrid"]!["unexpected"] = true);
        await AssertFixtureMutationInvalidAsync(9, json => json["modGrid"]!["stateFilter"] = 0);
    }

    [TestMethod]
    public async Task Save_PersistsDefinedManualWorkshopPolicy()
    {
        var root = TemporaryRoot();
        var paths = new TestPaths(root);
        try
        {
            var repository = new JsonSettingsRepository(paths);
            var settings = Settings("C:\\Games\\XCOM 2") with { WorkshopStartupRefresh = WorkshopStartupRefreshPolicy.Manual };

            var saved = await repository.SaveAsync(settings, TestContext.CancellationToken);
            var loaded = await repository.LoadAsync(TestContext.CancellationToken);

            saved.IsSuccess.Should().BeTrue(saved.Error?.Message);
            loaded.Value!.WorkshopStartupRefresh.Should().Be(WorkshopStartupRefreshPolicy.Manual);
            JObject.Parse(await File.ReadAllTextAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), TestContext.CancellationToken))
                .Value<string>("workshopStartupRefresh").Should().Be("Manual");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task AutoSavePreference_RoundTripsAcrossRepositoryRestart()
    {
        var root = TemporaryRoot();
        var paths = new TestPaths(root);
        try
        {
            var saved = await new JsonSettingsRepository(paths).SaveAsync(Settings("C:\\Games\\XCOM 2") with { AutoSaveChanges = true }, TestContext.CancellationToken);
            var loaded = await new JsonSettingsRepository(paths).LoadAsync(TestContext.CancellationToken);

            saved.IsSuccess.Should().BeTrue(saved.Error?.Message);
            loaded.Value!.AutoSaveChanges.Should().BeTrue();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task MigrationRewrite_IsAtomicBackedUpAndIdempotent()
    {
        var root = TemporaryRoot();
        var paths = new TestPaths(root);
        try
        {
            var path = await SeedFixtureAsync(paths, 6);
            var original = await File.ReadAllBytesAsync(path, TestContext.CancellationToken);
            var repository = new JsonSettingsRepository(paths);

            (await repository.LoadAsync(TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            var canonical = await File.ReadAllBytesAsync(path, TestContext.CancellationToken);
            var backup = await File.ReadAllBytesAsync(path + ".bak", TestContext.CancellationToken);
            backup.Should().Equal(original);
            canonical.Should().NotEqual(original);

            (await repository.LoadAsync(TestContext.CancellationToken)).IsSuccess.Should().BeTrue();

            repository.LastLoadReport.Should().Be(new SettingsLoadReport(9, false, false));
            (await File.ReadAllBytesAsync(path, TestContext.CancellationToken)).Should().Equal(canonical);
            (await File.ReadAllBytesAsync(path + ".bak", TestContext.CancellationToken)).Should().Equal(backup);
            Directory.EnumerateFiles(paths.ConfigurationDirectory, "*.tmp").Should().BeEmpty();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FailedCanonicalRewrite_ReportsFailureButReturnsMigratedSettings()
    {
        var root = TemporaryRoot();
        var paths = new TestPaths(root);
        try
        {
            var path = await SeedFixtureAsync(paths, 6);
            await using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var repository = new JsonSettingsRepository(paths);

            var loaded = await repository.LoadAsync(TestContext.CancellationToken);

            loaded.IsSuccess.Should().BeTrue();
            loaded.Value!.SchemaVersion.Should().Be(9);
            repository.LastLoadReport!.CanonicalRewriteAttempted.Should().BeTrue();
            repository.LastLoadReport.CanonicalRewriteSucceeded.Should().BeFalse();
            repository.LastLoadReport.RewriteError!.Code.Should().Be("settings.write_failed");
            JObject.Parse(await File.ReadAllTextAsync(path, TestContext.CancellationToken)).Value<int>("schemaVersion").Should().Be(6);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SaveLoadAndBackup_AreAtomicTelemetryFreeAndCurrentDirectoryIndependent()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML.Settings", Guid.NewGuid().ToString("N"));
        var paths = new TestPaths(root);
        var repository = new JsonSettingsRepository(paths);
        var first = Settings("C:\\Games\\XCOM 2");
        var second = Settings("C:\\Games\\XCOM 2 Updated");
        try
        {
            (await repository.SaveAsync(first, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            (await repository.SaveAsync(second, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();

            var loaded = await repository.LoadAsync(TestContext.CancellationToken);
            loaded.Value.Should().BeEquivalentTo(second);
            File.Exists(Path.Combine(paths.ConfigurationDirectory, "settings.json.bak")).Should().BeTrue();
            var json = await File.ReadAllTextAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), TestContext.CancellationToken);
            json.Should().NotContainEquivalentOf("sentry").And.NotContainEquivalentOf("telemetry").And.NotContain("Guid").And.NotContain("UserName");
            JObject.Parse(json).Value<string>("navigationRailMode").Should().Be("Compact");
            JObject.Parse(json).Value<bool>("autoSaveChanges").Should().BeFalse();
            Directory.EnumerateFiles(paths.ConfigurationDirectory, "*.tmp").Should().BeEmpty();
            var bytes = await File.ReadAllBytesAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), TestContext.CancellationToken);
            Convert.ToHexString(bytes.AsSpan(0, Math.Min(3, bytes.Length))).Should().NotBe(Convert.ToHexString(Encoding.UTF8.GetPreamble()));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task Backup_ContainsPreviousGenerationAndRecoversCorruptPrimary()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML.Settings", Guid.NewGuid().ToString("N"));
        var paths = new TestPaths(root);
        var repository = new JsonSettingsRepository(paths);
        var first = Settings("C:\\Games\\First");
        var second = Settings("C:\\Games\\Second");
        try
        {
            (await repository.SaveAsync(first, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            (await repository.SaveAsync(second, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), "{ corrupt", TestContext.CancellationToken);

            var recovered = await repository.LoadAsync(TestContext.CancellationToken);

            recovered.IsSuccess.Should().BeTrue();
            recovered.Value.Should().BeEquivalentTo(first);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task PreCancelledLoad_PreservesCancellationInsteadOfRecoveryFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML.Settings", Guid.NewGuid().ToString("N"));
        var repository = new JsonSettingsRepository(new TestPaths(root));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var result = await repository.LoadAsync(source.Token);

        result.Error!.Code.Should().Be("settings.cancelled");
    }

    [TestMethod]
    public async Task VersionOneEmptyArguments_MigrateToLegacySafeDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML.Settings", Guid.NewGuid().ToString("N"));
        var paths = new TestPaths(root);
        try
        {
            Directory.CreateDirectory(paths.ConfigurationDirectory);
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), """
                { "schemaVersion": 1, "selectedGame": "XCom2", "modRootLocations": [], "launchArguments": [], "modIntents": [], "categories": [], "tags": [] }
                """, TestContext.CancellationToken);

            var loaded = await new JsonSettingsRepository(paths).LoadAsync(TestContext.CancellationToken);

            loaded.Value!.SchemaVersion.Should().Be(ApplicationSettingsDefaults.CurrentSchemaVersion);
            loaded.Value.LaunchArguments.Select(argument => argument.Value).Should().Equal("-review", "-noRedScreens");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task VersionTwoEmptyArguments_RemainAnExplicitUserChoice()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML.Settings", Guid.NewGuid().ToString("N"));
        var paths = new TestPaths(root);
        try
        {
            Directory.CreateDirectory(paths.ConfigurationDirectory);
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigurationDirectory, "settings.json"), """
                { "schemaVersion": 2, "selectedGame": "XCom2", "modRootLocations": [], "launchArguments": [], "modIntents": [], "categories": [], "tags": [] }
                """, TestContext.CancellationToken);

            var loaded = await new JsonSettingsRepository(paths).LoadAsync(TestContext.CancellationToken);

            loaded.Value!.LaunchArguments.Should().BeEmpty();
            loaded.Value.AllowLaunchWithMissingDependencies.Should().BeFalse();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public TestContext TestContext { get; set; }

    private async Task AssertFixtureMutationInvalidAsync(int schema, Action<JObject> mutate)
    {
        var root = TemporaryRoot();
        var paths = new TestPaths(root);
        try
        {
            var path = await SeedFixtureAsync(paths, schema);
            var json = JObject.Parse(await File.ReadAllTextAsync(path, TestContext.CancellationToken));
            mutate(json);
            await File.WriteAllTextAsync(path, json.ToString(), TestContext.CancellationToken);

            var loaded = await new JsonSettingsRepository(paths).LoadAsync(TestContext.CancellationToken);

            loaded.IsSuccess.Should().BeFalse();
            loaded.Error!.Code.Should().Be("settings.recovery_failed");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string TemporaryRoot() => Path.Combine(Path.GetTempPath(), "AAML.Settings", Guid.NewGuid().ToString("N"));

    private static async Task<string> SeedFixtureAsync(TestPaths paths, int schema)
    {
        Directory.CreateDirectory(paths.ConfigurationDirectory);
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestAssets", "Compatibility", "Settings", $"schema-v{schema}.json");
        var destination = Path.Combine(paths.ConfigurationDirectory, "settings.json");
        await File.WriteAllBytesAsync(destination, await File.ReadAllBytesAsync(fixture));
        return destination;
    }

    private static ApplicationSettings Settings(string gamePath) => new(
        ApplicationSettingsDefaults.CurrentSchemaVersion,
        GameVariant.XCom2,
        gamePath,
        ["C:\\Mods"],
        [new LaunchArgument("-Name=Unicode Ω")],
        [],
        [],
        [],
        true,
        new Dictionary<GameVariant, GameLocationSettings>
        {
            [GameVariant.XCom2] = new(gamePath, ["C:\\Mods"]),
            [GameVariant.ChimeraSquad] = new("I:\\Chimera", [])
        },
        true,
        WorkshopStartupRefreshPolicy.ActiveMods,
        ThemePreference.Dark,
        true,
        [new DuplicatePreference(new AAML.Domain.Mods.PackageId("SharedPackage"), new AAML.Domain.Mods.ModKey(AAML.Domain.Mods.ModSource.Manual, "C:\\Mods\\Preferred"))],
        new ModGridPreferences(false, AAML.Application.Mods.Grid.ModGridSemanticState.Duplicate, true, new HashSet<AAML.Application.Mods.Grid.ModGridGroupKey> { new("category", "gameplay") }),
        [],
        NavigationRailMode: NavigationRailMode.Compact);

    private sealed class TestPaths(string root) : AAML.Application.Ports.IApplicationPaths
    {
        public string ConfigurationDirectory { get; } = Path.Combine(root, "Config");
        public string DataDirectory { get; } = Path.Combine(root, "Data");
        public string StateDirectory { get; } = Path.Combine(root, "State");
        public string CacheDirectory { get; } = Path.Combine(root, "Cache");
        public string RuntimeDirectory { get; } = Path.Combine(root, "Runtime");
    }
}
