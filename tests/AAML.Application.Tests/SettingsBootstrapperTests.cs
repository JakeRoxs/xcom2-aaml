using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Application.Startup;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using FluentAssertions;

namespace AAML.Application.Tests;

[TestClass]
public sealed class SettingsBootstrapperTests
{
    [TestMethod]
    public async Task ExistingSettings_LoadWithoutMigrationOrSave()
    {
        var settings = Settings(GameVariant.ChimeraSquad);
        var repository = new FakeRepository { LoadResult = Result<ApplicationSettings>.Success(settings) };
        var importer = new FakeImporter { Result = Result<ApplicationSettings?>.Success(Settings(GameVariant.XCom2)) };

        var result = await new SettingsBootstrapper(repository, importer).InitializeAsync(TestContext.CancellationToken);

        result.Value!.Origin.Should().Be(SettingsOrigin.Existing);
        result.Value.Settings.Should().Be(settings);
        importer.Calls.Should().Be(0);
        repository.Saved.Should().BeNull();
    }

    [TestMethod]
    public async Task MissingSettings_MigrateLegacyAndPersistOnce()
    {
        var migrated = Settings(GameVariant.XCom2WarOfTheChosen);
        var repository = MissingRepository();
        var importer = new FakeImporter { Result = Result<ApplicationSettings?>.Success(migrated) };

        var result = await new SettingsBootstrapper(repository, importer).InitializeAsync(TestContext.CancellationToken);

        result.Value!.Origin.Should().Be(SettingsOrigin.MigratedLegacy);
        repository.Saved.Should().Be(migrated);
    }

    [TestMethod]
    public async Task NoLegacySettings_CreatesAndPersistsMinimalDefault()
    {
        var repository = MissingRepository();
        var importer = new FakeImporter { Result = Result<ApplicationSettings?>.Success(null) };

        var result = await new SettingsBootstrapper(repository, importer).InitializeAsync(TestContext.CancellationToken);

        result.Value!.Origin.Should().Be(SettingsOrigin.CreatedDefault);
        result.Value.Settings.SelectedGame.Should().Be(GameVariant.XCom2);
        result.Value.Settings.LaunchArguments.Select(argument => argument.Value).Should().Equal("-review", "-noRedScreens");
        repository.Saved.Should().Be(result.Value.Settings);
    }

    [TestMethod]
    public async Task InvalidModernSettings_FailWithoutSilentlyMigratingOrReplacing()
    {
        var error = new Error("settings.invalid", "Invalid.", ErrorKind.InvalidData);
        var repository = new FakeRepository { LoadResult = Result<ApplicationSettings>.Failure(error) };
        var importer = new FakeImporter { Result = Result<ApplicationSettings?>.Success(null) };

        var result = await new SettingsBootstrapper(repository, importer).InitializeAsync(TestContext.CancellationToken);

        result.Error.Should().Be(error);
        importer.Calls.Should().Be(0);
        repository.Saved.Should().BeNull();
    }

    [TestMethod]
    public async Task SelectingGame_PersistsUpdatedSettings()
    {
        var repository = MissingRepository();
        var service = new SettingsBootstrapper(repository, new FakeImporter());

        var result = await service.SelectGameAsync(Settings(GameVariant.XCom2), GameVariant.ChimeraSquad, TestContext.CancellationToken);

        result.Value!.SelectedGame.Should().Be(GameVariant.ChimeraSquad);
        repository.Saved!.SelectedGame.Should().Be(GameVariant.ChimeraSquad);
    }

    [TestMethod]
    public async Task SettingGameInstallation_PersistsPathWithSpacesAndUnicode()
    {
        var repository = MissingRepository();
        var service = new SettingsBootstrapper(repository, new FakeImporter());

        var result = await service.SetGameInstallationAsync(Settings(GameVariant.XCom2), "  C:\\Games Ω\\XCOM 2  ", TestContext.CancellationToken);

        result.Value!.GameInstallationLocation.Should().Be("C:\\Games Ω\\XCOM 2");
        repository.Saved.Should().Be(result.Value);
    }

    [TestMethod]
    public async Task AllowingLaunchWithMissingDependencies_PersistsOptIn()
    {
        var repository = MissingRepository();
        var service = new SettingsBootstrapper(repository, new FakeImporter());

        var result = await service.SetAllowLaunchWithMissingDependenciesAsync(Settings(GameVariant.XCom2), true, TestContext.CancellationToken);

        result.Value!.AllowLaunchWithMissingDependencies.Should().BeTrue();
        repository.Saved.Should().Be(result.Value);
    }

    [TestMethod]
    public async Task GameSwitch_RetainsIndependentLocationsAndRoots()
    {
        var repository = MissingRepository();
        var service = new SettingsBootstrapper(repository, new FakeImporter());
        var xcom = Settings(GameVariant.XCom2) with { GameInstallationLocation = "C:\\XCOM2", ModRootLocations = ["C:\\Workshop\\268500"] };

        var chimera = await service.SelectGameAsync(xcom, GameVariant.ChimeraSquad, TestContext.CancellationToken);
        var chimeraSaved = await service.SetGameInstallationAsync(chimera.Value!, "I:\\Chimera", TestContext.CancellationToken);
        var restored = await service.SelectGameAsync(chimeraSaved.Value!, GameVariant.XCom2, TestContext.CancellationToken);

        chimera.Value!.GameInstallationLocation.Should().BeNull();
        restored.Value!.GameInstallationLocation.Should().Be("C:\\XCOM2");
        restored.Value.ModRootLocations.Should().Equal("C:\\Workshop\\268500");
        restored.Value.LocationFor(GameVariant.ChimeraSquad).InstallationLocation.Should().Be("I:\\Chimera");
    }

    [TestMethod]
    public async Task Preferences_PersistArgumentsRootsAndLaunchBehavior()
    {
        var root = Path.Combine(Path.GetTempPath(), "AAML Manual Root", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var repository = MissingRepository();
            var service = new SettingsBootstrapper(repository, new FakeImporter());
            var result = await service.SavePreferencesAsync(Settings(GameVariant.XCom2), [new LaunchArgument("-log"), new LaunchArgument("-Name=Two Words")], [root], true, true, WorkshopStartupRefreshPolicy.ActiveMods, ThemePreference.Dark, true, false, UpdateChannelPreference.Prerelease, 1.25m, 1.40m, TestContext.CancellationToken);

            result.Value!.LaunchArguments.Select(argument => argument.Value).Should().Equal("-log", "-Name=Two Words");
            result.Value.ModRootLocations.Should().Equal(Path.GetFullPath(root));
            result.Value.AllowLaunchWithMissingDependencies.Should().BeTrue();
            result.Value.CloseAfterLaunch.Should().BeTrue();
            result.Value.WorkshopStartupRefresh.Should().Be(WorkshopStartupRefreshPolicy.ActiveMods);
            result.Value.Theme.Should().Be(ThemePreference.Dark);
            result.Value.CheckForUpdates.Should().BeFalse();
            result.Value.UpdateChannel.Should().Be(UpdateChannelPreference.Prerelease);
            result.Value.AllowMultipleInstances.Should().BeTrue();
            result.Value.TextScale.Should().Be(1.25m);
            result.Value.IconScale.Should().Be(1.40m);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Preferences_RejectOutOfRangeAccessibilityScalesBeforeSave()
    {
        var repository = MissingRepository();
        var service = new SettingsBootstrapper(repository, new FakeImporter());

        var invalidText = await service.SavePreferencesAsync(Settings(GameVariant.XCom2), [], [], false, false, WorkshopStartupRefreshPolicy.AllMods, ThemePreference.System, false, true, UpdateChannelPreference.Stable, 1.51m, 1m, TestContext.CancellationToken);
        var invalidIcon = await service.SavePreferencesAsync(Settings(GameVariant.XCom2), [], [], false, false, WorkshopStartupRefreshPolicy.AllMods, ThemePreference.System, false, true, UpdateChannelPreference.Stable, 1m, 0.74m, TestContext.CancellationToken);

        invalidText.Error!.Code.Should().Be("settings.text_scale_invalid");
        invalidIcon.Error!.Code.Should().Be("settings.icon_scale_invalid");
    }

    [TestMethod]
    public async Task NavigationRailMode_PersistsOnlyRequestedField()
    {
        var repository = MissingRepository();
        var service = new SettingsBootstrapper(repository, new FakeImporter());
        var original = Settings(GameVariant.XCom2) with { Theme = ThemePreference.Dark, CloseAfterLaunch = true };

        var result = await service.SetNavigationRailModeAsync(original, NavigationRailMode.Compact, TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value.Should().Be(original with { NavigationRailMode = NavigationRailMode.Compact });
        repository.Saved.Should().Be(result.Value);
    }

    [TestMethod]
    public async Task AutoSaveChanges_PersistsOnlyRequestedField()
    {
        var repository = MissingRepository();
        var service = new SettingsBootstrapper(repository, new FakeImporter());
        var original = Settings(GameVariant.XCom2) with { Theme = ThemePreference.Dark, CloseAfterLaunch = true };

        var result = await service.SetAutoSaveChangesAsync(original, true, TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        result.Value.Should().Be(original with { AutoSaveChanges = true });
        repository.Saved.Should().Be(result.Value);
    }

    public TestContext TestContext { get; set; }

    private static FakeRepository MissingRepository() => new()
    {
        LoadResult = Result<ApplicationSettings>.Failure(new Error("settings.not_found", "Missing.", ErrorKind.NotFound))
    };

    private static ApplicationSettings Settings(GameVariant game) => new(ApplicationSettingsDefaults.CurrentSchemaVersion, game, null, [], [], [], [], []);

    private sealed class FakeRepository : ISettingsRepository
    {
        public required Result<ApplicationSettings> LoadResult { get; init; }
        public ApplicationSettings? Saved { get; private set; }
        public Task<Result<ApplicationSettings>> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(LoadResult);
        public Task<Result> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken) { Saved = settings; return Task.FromResult(Result.Success()); }
    }

    private sealed class FakeImporter : ILegacySettingsImporter
    {
        public Result<ApplicationSettings?> Result { get; init; } = Result<ApplicationSettings?>.Success(null);
        public int Calls { get; private set; }
        public Task<Result<ApplicationSettings?>> TryImportAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult(Result); }
    }
}
