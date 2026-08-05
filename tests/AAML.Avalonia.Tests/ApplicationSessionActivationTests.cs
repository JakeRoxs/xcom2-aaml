using AAML.Application.Common;
using AAML.Application.Configurations;
using AAML.Application.Diagnostics;
using AAML.Application.Launching;
using AAML.Application.Logging;
using AAML.Application.Mods;
using AAML.Application.Mods.Grid;
using AAML.Application.Mods.Conflicts;
using AAML.Application.Mods.Dependencies;
using AAML.Application.Mods.Duplicates;
using AAML.Application.Mods.Workshop;
using AAML.Application.Ports;
using AAML.Application.Profiles;
using AAML.Application.Settings;
using AAML.Application.Startup;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Domain.Profiles;
using FluentAssertions;
using Moq;
using Reactive.Bindings;
using System.Reactive.Linq;
using Zafiro.UI.Navigation.Sections;
using Zafiro.UI.Shell;

namespace AAML.Avalonia.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ApplicationSessionActivationTests
{
    [TestMethod]
    public async Task ApplyConfiguration_BlocksUnconfirmedExistingRootPreviewBeforeWriting()
    {
        var fixture = new SessionFixture();
        fixture.ReleaseDiscovery();
        (await fixture.Session.InitializeAsync(TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
        fixture.RootGuard.Register(new ExistingModRootPreview(GameVariant.XCom2WarOfTheChosen, "C:\\Game", "XComEngine.ini", "hash", "Windows", [new(0, "missing", null, 1, ExistingModRootResolution.Missing)], "report"));

        var result = await fixture.Session.ApplyConfigurationAsync(TestContext.CancellationToken);

        result.Error!.Code.Should().Be("mod_roots.preview_unconfirmed");
        fixture.Session.Status.Should().Contain("Migration");
    }

    [TestMethod]
    public async Task DraftCheckboxAndBulkActivation_ProfileCapturesVisibleMembershipAndOrderWithoutSavingSettings()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        fixture.Session.ModRows.Single(row => row.Key == fixture.First.Key).IsActive = true;
        fixture.Session.ModRows.Single(row => row.Key == fixture.First.Key).Order = 1;
        fixture.Session.SetSelectedActive(new HashSet<ModKey> { fixture.Second.Key }, true).Value.Should().Be(1);
        fixture.Session.ModRows.Single(row => row.Key == fixture.Second.Key).Order = 0;

        var result = await fixture.Session.CreateProfileAsync("Draft", TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        fixture.CreatedProfile!.Mods.Select(mod => mod.PackageId).Should().Equal(fixture.Second.PackageId, fixture.First.PackageId);
        fixture.SettingsRepository.Saved.Should().BeNull("profile creation must not persist the global activation draft");
        fixture.Session.HasUnsavedModDrafts.Should().BeTrue();
        fixture.Session.UnsavedModDraftCount.Should().Be(2);
    }

    [TestMethod]
    public async Task DraftActivation_LaunchAutoSavesAndRequestsOnlyActiveMods()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        fixture.Session.SetSelectedActive(new HashSet<ModKey> { fixture.Second.Key }, true).IsSuccess.Should().BeTrue();

        var result = await fixture.Session.LaunchAsync(TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        fixture.SettingsRepository.Saved!.ModIntents.Should().ContainSingle(intent => intent.Mod == fixture.Second.Key && intent.IsActive);
        fixture.LaunchRequest!.ActiveMods.Should().ContainSingle(mod => mod.Mod == fixture.Second.Key);
        fixture.LaunchRequest.ActiveMods.Should().NotContain(mod => mod.Mod == fixture.First.Key);
        fixture.Session.HasUnsavedModDrafts.Should().BeFalse();
    }

    [TestMethod]
    public async Task BulkActivation_EmptyAndAllSkippedSelectionsFailWithReasons()
    {
        var fixture = new SessionFixture(duplicatePackages: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);

        fixture.Session.SetSelectedActive(new HashSet<ModKey>(), true).Error!.Code.Should().Be("mods.selection_empty");
        var skipped = fixture.Session.SetSelectedActive(new HashSet<ModKey> { fixture.First.Key, new(ModSource.Manual, "missing") }, true);

        skipped.Error!.Code.Should().Be("mods.activation_no_changes");
        skipped.Error.Message.Should().Contain("duplicate").And.Contain("missing");
    }

    [TestMethod]
    public async Task PreviewSelection_AutomaticallyUpdatesFromWorkshopCacheAndClears()
    {
        var fixture = new SessionFixture(previewFlow: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        using var viewModel = new ModsViewModel(fixture.Session, Mock.Of<IExternalLauncher>());
        var row = fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key);

        viewModel.SetSelection([row]);
        await Task.Delay(50, TestContext.CancellationToken);
        viewModel.SelectedPreviewImagePath.Should().Be("C:\\Cache\\workshop.png");
        viewModel.SetSelection([]);
        viewModel.SelectedPreviewImagePath.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ManualWorkshopPolicy_PerformsNoAutomaticStartupObservation()
    {
        var fixture = new SessionFixture(previewFlow: true, workshopPolicy: WorkshopStartupRefreshPolicy.Manual);

        var result = await fixture.Session.InitializeAsync(TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        fixture.WorkshopOperations.Verify(service => service.RefreshAsync(
            It.IsAny<IReadOnlyList<ModInstallation>>(),
            It.IsAny<IProgress<WorkshopOperationProgress>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task NavigationRailMode_PersistsAndUpdatesSessionWithoutChangingOtherSettings()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        var original = fixture.Session.Settings!;

        var result = await fixture.Session.SetNavigationRailModeAsync(NavigationRailMode.Compact, TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        fixture.NavigationRailSaved.Should().Be(original with { NavigationRailMode = NavigationRailMode.Compact });
        fixture.Session.Settings.Should().Be(fixture.NavigationRailSaved);
    }

    [TestMethod]
    public async Task NavigationRailMode_SaveFailureLeavesSessionSettingsUnchanged()
    {
        var fixture = new SessionFixture(navigationRailSaveFails: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        var original = fixture.Session.Settings;

        var result = await fixture.Session.SetNavigationRailModeAsync(NavigationRailMode.Compact, TestContext.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        fixture.Session.Settings.Should().BeSameAs(original);
        fixture.Session.Status.Should().Be("Rail mode write failed.");
    }

    [TestMethod]
    public async Task DirectRowEdits_AutoSaveDebounceCoalescesLatestSnapshot()
    {
        var fixture = new SessionFixture(autoSave: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        var row = fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key);

        row.IsActive = true;
        row.Order = 3;
        row.Order = 7;

        await WaitUntilAsync(() => fixture.SettingsRepository.SaveCount == 1 && !fixture.Session.HasUnsavedModDrafts);
        fixture.SettingsRepository.Saved!.ModIntents.Should().ContainSingle(intent => intent.Mod == fixture.First.Key && intent.IsActive && intent.ExplicitOrder == 7);
        await Task.Delay(500, TestContext.CancellationToken);
        fixture.SettingsRepository.SaveCount.Should().Be(1);
    }

    [TestMethod]
    public async Task DashboardPreferenceEdits_PreviewThemeAndDebounceOneDurableGlobalSave()
    {
        var fixture = new SessionFixture(autoSave: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        var ui = new Mock<IApplicationUiController>();
        using var viewModel = new DashboardViewModel(fixture.Session, ui.Object, PresetService());
        viewModel.Activate();

        viewModel.LaunchArguments = "-review";
        viewModel.AllowLaunchWithMissingDependencies = true;
        viewModel.CloseAfterLaunch = true;
        viewModel.WorkshopStartupRefresh = WorkshopStartupRefreshPolicy.ActiveMods;
        viewModel.Theme = ThemePreference.Dark;
        viewModel.AllowMultipleInstances = true;
        viewModel.CheckForUpdates = true;
        viewModel.UpdateChannel = UpdateChannelPreference.Prerelease;

        ui.Verify(service => service.ApplyTheme(ThemePreference.Dark), Times.Once);
        await WaitUntilAsync(() => fixture.PreferencesSaveCount == 1);
        fixture.PreferencesSaved.Should().NotBeNull();
        fixture.PreferencesSaved!.LaunchArguments.Select(argument => argument.Value).Should().Equal("-review");
        fixture.PreferencesSaved.AllowLaunchWithMissingDependencies.Should().BeTrue();
        fixture.PreferencesSaved.CloseAfterLaunch.Should().BeTrue();
        fixture.PreferencesSaved.WorkshopStartupRefresh.Should().Be(WorkshopStartupRefreshPolicy.ActiveMods);
        fixture.PreferencesSaved.Theme.Should().Be(ThemePreference.Dark);
        fixture.PreferencesSaved.AllowMultipleInstances.Should().BeTrue();
        fixture.PreferencesSaved.CheckForUpdates.Should().BeTrue();
        fixture.PreferencesSaved.UpdateChannel.Should().Be(UpdateChannelPreference.Prerelease);
        await Task.Delay(500, TestContext.CancellationToken);
        fixture.PreferencesSaveCount.Should().Be(1);
    }

    [TestMethod]
    public async Task ModGridDirectPreferences_AutoSaveDebouncesWhileManualSaveViewFlushes()
    {
        var fixture = new SessionFixture(autoSave: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        using var viewModel = new ModsViewModel(fixture.Session, Mock.Of<IExternalLauncher>());
        viewModel.Activate();

        viewModel.IncludeHidden = false;
        viewModel.GroupByCategory = true;

        await WaitUntilAsync(() => fixture.ModGridSaveCount == 1);
        fixture.ModGridSaved.Should().BeEquivalentTo(new ModGridPreferences(false, null, true, new HashSet<ModGridGroupKey>()));
        viewModel.IncludeHidden = true;
        (await fixture.Session.SaveModGridPreferencesAsync(TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
        fixture.ModGridSaveCount.Should().Be(2);
    }

    [TestMethod]
    public async Task BulkActivation_AutoSavesImmediatelyAfterApplicableMutation()
    {
        var fixture = new SessionFixture(autoSave: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);

        var result = await fixture.Session.SetSelectedActiveAndSaveAsync(new HashSet<ModKey> { fixture.Second.Key }, true, TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        fixture.SettingsRepository.SaveCount.Should().Be(1);
        fixture.Session.HasUnsavedModDrafts.Should().BeFalse();
    }

    [TestMethod]
    public async Task ActivateDeactivateMoveAndRenumber_AutoSaveEachApplicableBulkMutationImmediately()
    {
        var fixture = new SessionFixture(autoSave: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);

        (await fixture.Session.SetSelectedActiveAndSaveAsync(new HashSet<ModKey> { fixture.First.Key }, true, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
        (await fixture.Session.SetSelectedActiveAndSaveAsync(new HashSet<ModKey> { fixture.First.Key }, false, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
        (await fixture.Session.MoveSelectedAndSaveAsync(new HashSet<ModKey> { fixture.Second.Key }, -1, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).Order = 8;
        (await fixture.Session.RenumberModsAndSaveAsync(TestContext.CancellationToken)).IsSuccess.Should().BeTrue();

        fixture.SettingsRepository.SaveCount.Should().Be(4);
        fixture.Session.HasUnsavedModDrafts.Should().BeFalse();
    }

    [TestMethod]
    public async Task Discard_CancelsPendingDebounceAndRestoresCleanDrafts()
    {
        var fixture = new SessionFixture(autoSave: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).IsActive = true;

        fixture.Session.DiscardModDrafts();
        await Task.Delay(550, TestContext.CancellationToken);

        fixture.SettingsRepository.SaveCount.Should().Be(0);
        fixture.Session.HasUnsavedModDrafts.Should().BeFalse();
    }

    [TestMethod]
    public async Task DisposingSession_CancelsPendingAutoSaveTimer()
    {
        var fixture = new SessionFixture(autoSave: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).IsActive = true;

        fixture.Session.Dispose();
        await Task.Delay(550, TestContext.CancellationToken);

        fixture.SettingsRepository.SaveCount.Should().Be(0);
    }

    [TestMethod]
    public async Task SaveFailure_RetainsDirtyDraftAndVisibleError()
    {
        var fixture = new SessionFixture(autoSave: true, modSaveFails: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).IsActive = true;

        await WaitUntilAsync(() => fixture.SettingsRepository.SaveCount == 1 && fixture.Session.Status == "Mod settings write failed.");

        fixture.Session.HasUnsavedModDrafts.Should().BeTrue();
        fixture.Session.Status.Should().Be("Mod settings write failed.");
    }

    [TestMethod]
    public async Task OlderSaveCompletion_DoesNotMarkNewerDraftClean()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new SessionFixture(modSaveRelease: release);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).IsActive = true;
        var save = fixture.Session.SaveModDraftsAsync(TestContext.CancellationToken);
        await fixture.SettingsRepository.SaveStarted.Task.WaitAsync(TestContext.CancellationToken);

        fixture.Session.ModRows.Single(item => item.Key == fixture.Second.Key).IsActive = true;
        release.SetResult();
        (await save).IsSuccess.Should().BeTrue();

        fixture.SettingsRepository.Saved!.ModIntents.Should().ContainSingle(intent => intent.Mod == fixture.First.Key);
        fixture.Session.HasUnsavedModDrafts.Should().BeTrue();
        fixture.Session.UnsavedModDraftCount.Should().Be(1);
        fixture.Session.Status.Should().Contain("newer edit");
    }

    [TestMethod]
    public async Task EnablingAutoSave_PersistsPreferenceAndFlushesCurrentDraft()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).IsActive = true;
        fixture.Session.ActivateAutoSaveOwner("mods");

        var result = await fixture.Session.SetAutoSaveChangesAsync(true, TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        fixture.AutoSavePreferenceSaved.Should().BeTrue();
        fixture.Session.Settings!.AutoSaveChanges.Should().BeTrue();
        fixture.SettingsRepository.SaveCount.Should().Be(1);
        fixture.Session.HasUnsavedModDrafts.Should().BeFalse();
    }

    [TestMethod]
    public async Task DisablingAutoSave_PersistsPreferenceWithoutDiscardingDraft()
    {
        var fixture = new SessionFixture(autoSave: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).IsActive = true;

        var result = await fixture.Session.SetAutoSaveChangesAsync(false, TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue(result.Error?.Message);
        fixture.AutoSavePreferenceSaved.Should().BeFalse();
        fixture.SettingsRepository.SaveCount.Should().Be(0);
        fixture.Session.HasUnsavedModDrafts.Should().BeTrue();
    }

    [TestMethod]
    public async Task ViewModelToggle_PersistsThroughSession()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        using var viewModel = new DashboardViewModel(fixture.Session, Mock.Of<IApplicationUiController>(), PresetService());
        viewModel.Activate();

        viewModel.AutoSaveChanges = true;

        await WaitUntilAsync(() => fixture.AutoSavePreferenceSaved == true);
        fixture.Session.Settings!.AutoSaveChanges.Should().BeTrue();
        viewModel.AutoSaveChanges.Should().BeTrue();
    }

    private static ILaunchArgumentPresetService PresetService()
    {
        var repository = new Mock<ILegacyLaunchArgumentSuggestionRepository>();
        repository.Setup(port => port.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LegacyLaunchArgumentSuggestionReadResult(null, []));
        return new LaunchArgumentPresetService(repository.Object);
    }

    [TestMethod]
    public async Task PresetToggles_PreserveFreeformArgumentsAndUseCatalogOrderWithValuedArguments()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        using var viewModel = new DashboardViewModel(fixture.Session, Mock.Of<IApplicationUiController>(), PresetService());
        viewModel.LaunchArguments = "-freeform";
        var regenerate = viewModel.LaunchArgumentPresets.Single(option => option.Preset.Id == "regenerate-inis");
        var log = viewModel.LaunchArgumentPresets.Single(option => option.Preset.Id == "log");
        var language = viewModel.LaunchArgumentPresets.Single(option => option.Preset.Id == "language");

        regenerate.IsActive = true;
        log.IsActive = true;
        language.Value = "fr";
        language.IsActive = true;

        viewModel.LaunchArguments.Split(Environment.NewLine).Should().Equal("-freeform", "-log", "-language=fr", "-regenerateinis");
        language.Value = "de";
        viewModel.LaunchArguments.Split(Environment.NewLine).Should().Equal("-freeform", "-log", "-language=de", "-regenerateinis");
        language.IsActive = false;
        viewModel.LaunchArguments.Split(Environment.NewLine).Should().Equal("-freeform", "-log", "-regenerateinis");
    }

    [TestMethod]
    public async Task ChallengeMode_DoesNotOfferOrApplyConsolePreset()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        using var viewModel = new DashboardViewModel(fixture.Session, Mock.Of<IApplicationUiController>(), PresetService());
        viewModel.LaunchArguments = "-freeform";
        viewModel.SelectedGame = GameVariant.XCom2WarOfTheChosenChallengeMode;
        var console = viewModel.LaunchArgumentPresets.Single(option => option.Preset.Id == "allow-console");

        console.IsApplicable.Should().BeFalse();
        console.IsActive = true;

        viewModel.LaunchArguments.Should().Be("-freeform");
        console.IsActive.Should().BeFalse();
    }

    [TestMethod]
    public async Task ExistingEquivalentArgument_IsActiveRemovableAndNeverDuplicatedByPreset()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        using var viewModel = new DashboardViewModel(fixture.Session, Mock.Of<IApplicationUiController>(), PresetService());
        viewModel.LaunchArguments = "-LOG\n-noRedscreens\n-unrelated";
        var log = viewModel.LaunchArgumentPresets.Single(option => option.Preset.Id == "log");

        log.IsActive.Should().BeTrue();
        log.IsActive = true;
        log.IsActive = false;

        viewModel.LaunchArguments.Split(Environment.NewLine).Should().Equal("-noRedscreens", "-unrelated");
    }

    [TestMethod]
    public async Task ExistingPresetLines_ParticipateInCatalogOrderingAfterRestart()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        using var viewModel = new DashboardViewModel(fixture.Session, Mock.Of<IApplicationUiController>(), PresetService());
        viewModel.LaunchArguments = "-freeform\n-regenerateinis";

        viewModel.LaunchArgumentPresets.Single(option => option.Preset.Id == "log").IsActive = true;

        viewModel.LaunchArguments.Split(Environment.NewLine).Should().Equal("-freeform", "-log", "-regenerateinis");
    }

    [TestMethod]
    public async Task PresetDraft_UsesAutoSaveManualSaveAndDiscardSemantics()
    {
        var automatic = new SessionFixture(autoSave: true);
        await automatic.Session.InitializeAsync(TestContext.CancellationToken);
        using (var viewModel = new DashboardViewModel(automatic.Session, Mock.Of<IApplicationUiController>(), PresetService()))
        {
            viewModel.Activate();
            viewModel.LaunchArgumentPresets.Single(option => option.Preset.Id == "log").IsActive = true;
            await WaitUntilAsync(() => automatic.PreferencesSaveCount == 1);
            automatic.PreferencesSaved!.LaunchArguments.Select(argument => argument.Value).Should().Contain("-log");
        }

        var manual = new SessionFixture();
        await manual.Session.InitializeAsync(TestContext.CancellationToken);
        using var manualViewModel = new DashboardViewModel(manual.Session, Mock.Of<IApplicationUiController>(), PresetService());
        manualViewModel.Activate();
        manualViewModel.LaunchArgumentPresets.Single(option => option.Preset.Id == "log").IsActive = true;
        manual.PreferencesSaveCount.Should().Be(0);
        (await manualViewModel.SavePreferences.Execute().FirstAsync()).IsSuccess.Should().BeTrue();
        manual.PreferencesSaved!.LaunchArguments.Select(argument => argument.Value).Should().Contain("-log");
        manualViewModel.LaunchArgumentPresets.Single(option => option.Preset.Id == "regenerate-inis").IsActive = true;
        (await manualViewModel.DiscardPreferences.Execute().FirstAsync()).IsSuccess.Should().BeTrue();
        manualViewModel.LaunchArguments.Should().NotContain("-regenerateinis");
    }

    [TestMethod]
    public async Task ConcurrentFlushRequests_AreSerializedAndPersistLatestDraft()
    {
        var fixture = new SessionFixture(autoSave: true, modSaveDelay: TimeSpan.FromMilliseconds(150));
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);

        var first = fixture.Session.SetSelectedActiveAndSaveAsync(new HashSet<ModKey> { fixture.First.Key }, true, TestContext.CancellationToken);
        await fixture.SettingsRepository.SaveStarted.Task.WaitAsync(TestContext.CancellationToken);
        var second = fixture.Session.SetSelectedActiveAndSaveAsync(new HashSet<ModKey> { fixture.Second.Key }, true, TestContext.CancellationToken);
        await Task.WhenAll(first, second);

        fixture.SettingsRepository.MaximumConcurrentSaves.Should().Be(1);
        fixture.SettingsRepository.SaveCount.Should().Be(2);
        fixture.SettingsRepository.Saved!.ModIntents.Should().Contain(intent => intent.Mod == fixture.First.Key && intent.IsActive)
            .And.Contain(intent => intent.Mod == fixture.Second.Key && intent.IsActive);
        fixture.Session.HasUnsavedModDrafts.Should().BeFalse();
    }

    [TestMethod]
    public async Task PersistedCompact_IsAppliedBeforeShellFirstRender()
    {
        var settings = SessionFixture.CreateSettings(NavigationRailMode.Compact);
        var repository = new Mock<ISettingsRepository>();
        repository.Setup(service => service.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Result<ApplicationSettings>.Success(settings));

        var loaded = await ShellStartupSettings.LoadAsync(repository.Object, TestContext.CancellationToken);
        var isOpen = await AvaloniaTestHost.Session.Dispatch(
            () => new AamlShellView(loaded?.NavigationRailMode ?? NavigationRailMode.Expanded).IsRailOpen,
            TestContext.CancellationToken);

        isOpen.Should().BeFalse();
    }

    [TestMethod]
    public async Task PersistedExpanded_IsAppliedBeforeShellFirstRender()
    {
        var settings = SessionFixture.CreateSettings(NavigationRailMode.Expanded);
        var repository = new Mock<ISettingsRepository>();
        repository.Setup(service => service.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Result<ApplicationSettings>.Success(settings));

        var loaded = await ShellStartupSettings.LoadAsync(repository.Object, TestContext.CancellationToken);
        var isOpen = await AvaloniaTestHost.Session.Dispatch(
            () => new AamlShellView(loaded?.NavigationRailMode ?? NavigationRailMode.Expanded).IsRailOpen,
            TestContext.CancellationToken);

        isOpen.Should().BeTrue();
    }

    [TestMethod]
    public async Task MissingStartupSettings_DefaultsFirstRenderToExpanded()
    {
        var repository = new Mock<ISettingsRepository>();
        repository.Setup(service => service.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Result<ApplicationSettings>.Failure(new Error("settings.not_found", "Missing.", ErrorKind.NotFound)));

        var loaded = await ShellStartupSettings.LoadAsync(repository.Object, TestContext.CancellationToken);
        var isOpen = await AvaloniaTestHost.Session.Dispatch(
            () => new AamlShellView(loaded?.NavigationRailMode ?? NavigationRailMode.Expanded).IsRailOpen,
            TestContext.CancellationToken);

        isOpen.Should().BeTrue();
    }

    [TestMethod]
    public async Task ToggleDuringInitialization_IsNotOverwrittenByLateConfigure()
    {
        var fixture = new SessionFixture(navigationRailMode: NavigationRailMode.Compact, delayDiscovery: true);
        fixture.Session.PrimeSettings(fixture.InitialSettings);
        var shell = await AvaloniaTestHost.Session.Dispatch(() =>
        {
            var view = new AamlShellView(NavigationRailMode.Compact);
            view.Configure(fixture.Session);
            return view;
        }, TestContext.CancellationToken);
        var initialization = fixture.Session.InitializeAsync(TestContext.CancellationToken);

        fixture.DiscoveryStarted.Task.IsCompleted.Should().BeTrue();
        await AvaloniaTestHost.Session.Dispatch(() =>
        {
            shell.IsRailOpen = true;
            shell.Configure(fixture.Session);
            shell.IsRailOpen.Should().BeTrue();
        }, TestContext.CancellationToken);
        fixture.ReleaseDiscovery();
        (await initialization).IsSuccess.Should().BeTrue();

        fixture.NavigationRailSaved!.NavigationRailMode.Should().Be(NavigationRailMode.Expanded);
        fixture.Bootstrapper.Verify(service => service.InitializeAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task RailSaveFailure_KeepsUserSelectionAndLoadedSessionSettings()
    {
        var fixture = new SessionFixture(navigationRailSaveFails: true);
        fixture.Session.PrimeSettings(fixture.InitialSettings);
        await AvaloniaTestHost.Session.Dispatch(() =>
        {
            var shell = new AamlShellView(NavigationRailMode.Expanded);
            shell.Configure(fixture.Session);
            shell.IsRailOpen = false;
            shell.IsRailOpen.Should().BeFalse();
        }, TestContext.CancellationToken);
        fixture.Session.Settings!.NavigationRailMode.Should().Be(NavigationRailMode.Expanded);
        fixture.Session.Status.Should().Be("Rail mode write failed.");
    }

    [TestMethod]
    public async Task SectionNavigation_DoesNotChangeRailMode()
    {
        await AvaloniaTestHost.Session.Dispatch(() =>
        {
            var first = Mock.Of<ISection>();
            var second = Mock.Of<ISection>();
            using var level = new SectionLevel([first, second], first, _ => { });
            var hierarchicalShell = new Mock<IHierarchicalShell>();
            hierarchicalShell.SetupGet(shell => shell.RootLevel).Returns(level);
            hierarchicalShell.SetupGet(shell => shell.ChildLevels).Returns(new ReactiveProperty<IReadOnlyList<SectionLevel>>([]));
            hierarchicalShell.SetupGet(shell => shell.SelectedPath).Returns(new ReactiveProperty<IReadOnlyList<ISection>>([first]));
            var view = new AamlShellView(NavigationRailMode.Compact) { DataContext = hierarchicalShell.Object };

            level.SelectedSection.Value = second;

            view.IsRailOpen.Should().BeFalse();
        }, TestContext.CancellationToken);
    }

    public TestContext TestContext { get; set; }

    private sealed class SessionFixture
    {
        public SessionFixture(bool duplicatePackages = false, bool previewFlow = false, WorkshopStartupRefreshPolicy workshopPolicy = WorkshopStartupRefreshPolicy.AllMods, bool navigationRailSaveFails = false, NavigationRailMode navigationRailMode = NavigationRailMode.Expanded, bool delayDiscovery = false, bool autoSave = false, bool modSaveFails = false, TaskCompletionSource? modSaveRelease = null, TimeSpan? modSaveDelay = null)
        {
            First = previewFlow
                ? new ModInstallation(new(ModSource.SteamWorkshop, "C:\\Mods\\first"), new("First"), "First", new WorkshopId(42), false, DescriptorState.Enabled, null,
                    new ModInstallationMetadata("C:\\Mods\\first\\first.XComMod", null, null, [], "C:\\Mods\\first\\local.png", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
                : Installation("first", "First");
            Second = Installation("second", duplicatePackages ? "First" : "Second");
            InitialSettings = CreateSettings(navigationRailMode, workshopPolicy) with { AutoSaveChanges = autoSave };
            Bootstrapper = new Mock<ISettingsBootstrapper>();
            Bootstrapper.Setup(service => service.InitializeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Result<SettingsBootstrapResult>.Success(new(InitialSettings, SettingsOrigin.Existing)));
            Bootstrapper.Setup(service => service.SetNavigationRailModeAsync(It.IsAny<ApplicationSettings>(), It.IsAny<NavigationRailMode>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ApplicationSettings current, NavigationRailMode mode, CancellationToken _) =>
                {
                    if (navigationRailSaveFails) return Result<ApplicationSettings>.Failure(new Error("settings.write_failed", "Rail mode write failed.", ErrorKind.Io));
                    NavigationRailSaved = current with { NavigationRailMode = mode };
                    return Result<ApplicationSettings>.Success(NavigationRailSaved);
                });
            Bootstrapper.Setup(service => service.SetAutoSaveChangesAsync(It.IsAny<ApplicationSettings>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ApplicationSettings current, bool enabled, CancellationToken _) =>
                {
                    AutoSavePreferenceSaved = enabled;
                    return Result<ApplicationSettings>.Success(current with { AutoSaveChanges = enabled });
                });
            Bootstrapper.Setup(service => service.SavePreferencesAsync(It.IsAny<ApplicationSettings>(), It.IsAny<IReadOnlyList<LaunchArgument>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<WorkshopStartupRefreshPolicy>(), It.IsAny<ThemePreference>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<UpdateChannelPreference>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ApplicationSettings current, IReadOnlyList<LaunchArgument> arguments, IReadOnlyList<string> roots, bool allowMissing, bool closeAfter, WorkshopStartupRefreshPolicy workshop, ThemePreference theme, bool multiple, bool checkUpdates, UpdateChannelPreference channel, CancellationToken _) =>
                {
                    PreferencesSaveCount++;
                    PreferencesSaved = current with { LaunchArguments = arguments, ModRootLocations = roots, AllowLaunchWithMissingDependencies = allowMissing, CloseAfterLaunch = closeAfter, WorkshopStartupRefresh = workshop, Theme = theme, AllowMultipleInstances = multiple, CheckForUpdates = checkUpdates, UpdateChannel = channel };
                    return Result<ApplicationSettings>.Success(PreferencesSaved);
                });
            Bootstrapper.Setup(service => service.SaveModGridPreferencesAsync(It.IsAny<ApplicationSettings>(), It.IsAny<ModGridPreferences>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ApplicationSettings current, ModGridPreferences grid, CancellationToken _) =>
                {
                    ModGridSaveCount++;
                    ModGridSaved = grid;
                    return Result<ApplicationSettings>.Success(current with { ModGrid = grid });
                });
            var catalog = new Mock<IModCatalogSource>();
            catalog.Setup(service => service.DiscoverAsync(It.IsAny<IReadOnlyList<string>>(), null, It.IsAny<CancellationToken>())).Returns(async () =>
            {
                DiscoveryStarted.TrySetResult();
                if (delayDiscovery) await discoveryRelease.Task;
                return Result<IReadOnlyList<ModInstallation>>.Success([First, Second]);
            });
            SettingsRepository = new RecordingSettingsRepository(modSaveFails, modSaveRelease, modSaveDelay);
            var profiles = new Mock<IProfileService>();
            profiles.Setup(service => service.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Result<IReadOnlyList<ModProfile>>.Success([]));
            profiles.Setup(service => service.CreateAsync(It.IsAny<string>(), It.IsAny<ApplicationSettings>(), It.IsAny<IReadOnlyList<ModInstallation>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string name, ApplicationSettings effective, IReadOnlyList<ModInstallation> mods, CancellationToken _) =>
                {
                    var installed = mods.ToDictionary(mod => mod.Key);
                    CreatedProfile = new(new ProfileId(Guid.NewGuid()), name, effective.SelectedGame,
                        effective.ModIntents.Where(intent => intent.IsActive).OrderBy(intent => intent.ExplicitOrder).Select((intent, order) => new ProfileModEntry(intent.Mod.Source, installed[intent.Mod].PackageId, installed[intent.Mod].WorkshopId, order)).ToArray(), [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    return Result<ModProfile>.Success(CreatedProfile);
                });
            var dependencies = new Mock<IModDependencyService>();
            dependencies.Setup(service => service.EvaluateAsync(It.IsAny<IReadOnlyCollection<WorkshopId>>(), It.IsAny<IReadOnlyCollection<WorkshopId>>(), It.IsAny<IReadOnlyCollection<WorkshopId>>(), It.IsAny<IReadOnlyDictionary<WorkshopId, IReadOnlySet<WorkshopId>>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<ModDependencyReport>.Success(new([], new Dictionary<WorkshopId, IReadOnlyList<WorkshopId>>())));
            var conflicts = new Mock<IModConflictService>();
            conflicts.Setup(service => service.AnalyzeAsync(It.IsAny<IReadOnlyList<ModInstallation>>(), It.IsAny<IReadOnlySet<ModKey>>(), It.IsAny<CancellationToken>())).ReturnsAsync(EmptyConflicts());
            conflicts.Setup(service => service.SetActiveAsync(It.IsAny<IReadOnlySet<ModKey>>(), It.IsAny<CancellationToken>())).ReturnsAsync(EmptyConflicts());
            var documents = new Mock<IConfigurationDocumentCatalog>();
            documents.Setup(service => service.ListAsync(It.IsAny<IReadOnlyList<ModInstallation>>(), It.IsAny<GameVariant>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result<IReadOnlyList<ConfigurationDocumentSummary>>.Success([]));
            var launcher = new Mock<IGameLaunchCoordinator>();
            launcher.Setup(service => service.LaunchAsync(It.IsAny<GameLaunchRequest>(), It.IsAny<CancellationToken>())).Callback<GameLaunchRequest, CancellationToken>((request, _) => LaunchRequest = request)
                .ReturnsAsync(Result<GameLaunchOutcome>.Success(new(null, new(DateTimeOffset.UtcNow, 42, "game"))));
            var diagnostics = new Mock<IApplicationDiagnostics>();
            diagnostics.Setup(service => service.FlushAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success());
            var workshop = new Mock<IWorkshopService>();
            workshop.Setup(service => service.GetItemAsync(new WorkshopId(42), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<WorkshopItem?>.Success(new WorkshopItem(new WorkshopId(42), "First", [], PreviewUrl: "https://cdn.example.test/workshop.png")));
            var previewCache = new Mock<IWorkshopPreviewCache>();
            previewCache.Setup(service => service.GetAsync(new WorkshopId(42), "https://cdn.example.test/workshop.png", It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<string?>.Success("C:\\Cache\\workshop.png"));
            var workshopOperations = new Mock<IWorkshopOperationCoordinator>();
            workshopOperations.Setup(service => service.RefreshAsync(It.IsAny<IReadOnlyList<ModInstallation>>(), It.IsAny<IProgress<WorkshopOperationProgress>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WorkshopBatchResult([]));
            WorkshopOperations = workshopOperations;
            RootGuard = new ExistingModRootPreviewGuard();

            var services = new Dictionary<Type, object>
            {
                [typeof(ISettingsBootstrapper)] = Bootstrapper.Object, [typeof(IModCatalogSource)] = catalog.Object,
                [typeof(IGameLaunchCoordinator)] = launcher.Object, [typeof(IModIntentService)] = new ModIntentService(SettingsRepository),
                [typeof(IProfileService)] = profiles.Object, [typeof(IModDependencyService)] = dependencies.Object,
                [typeof(IModConflictService)] = conflicts.Object, [typeof(IConfigurationDocumentCatalog)] = documents.Object,
                [typeof(IModDuplicateAnalyzer)] = new ModDuplicateAnalyzer(), [typeof(IApplicationDiagnostics)] = diagnostics.Object,
                [typeof(IWorkshopService)] = workshop.Object, [typeof(IWorkshopPreviewCache)] = previewCache.Object,
                [typeof(IWorkshopOperationCoordinator)] = workshopOperations.Object,
                [typeof(IExistingModRootPreviewGuard)] = RootGuard
            };
            var constructor = typeof(ApplicationSession).GetConstructors().Single();
            Session = (ApplicationSession)constructor.Invoke(constructor.GetParameters().Select(parameter => services.GetValueOrDefault(parameter.ParameterType) ?? MockObject(parameter.ParameterType)).ToArray());
        }

        public ApplicationSession Session { get; }
        public ApplicationSettings InitialSettings { get; }
        public Mock<ISettingsBootstrapper> Bootstrapper { get; }
        public TaskCompletionSource DiscoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModInstallation First { get; }
        public ModInstallation Second { get; }
        public RecordingSettingsRepository SettingsRepository { get; }
        public Mock<IWorkshopOperationCoordinator> WorkshopOperations { get; }
        public ExistingModRootPreviewGuard RootGuard { get; }
        public ModProfile? CreatedProfile { get; private set; }
        public GameLaunchRequest? LaunchRequest { get; private set; }
        public ApplicationSettings? NavigationRailSaved { get; private set; }
        public bool? AutoSavePreferenceSaved { get; private set; }
        public int PreferencesSaveCount { get; private set; }
        public ApplicationSettings? PreferencesSaved { get; private set; }
        public int ModGridSaveCount { get; private set; }
        public ModGridPreferences? ModGridSaved { get; private set; }

        public void ReleaseDiscovery() => discoveryRelease.TrySetResult();

        public static ApplicationSettings CreateSettings(NavigationRailMode navigationRailMode, WorkshopStartupRefreshPolicy workshopPolicy = WorkshopStartupRefreshPolicy.AllMods) =>
            new(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2WarOfTheChosen,
                "C:\\Game", ["C:\\Mods"], [], [], [], [], WorkshopStartupRefresh: workshopPolicy, CheckForUpdates: false, NavigationRailMode: navigationRailMode);

        private readonly TaskCompletionSource discoveryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static Result<ModConflictReport> EmptyConflicts() => Result<ModConflictReport>.Success(new([], new HashSet<string>()));
        private static ModInstallation Installation(string location, string package) => new(new(ModSource.Manual, location), new(package), package, null, false, DescriptorState.Enabled, null);
        private static object MockObject(Type type)
        {
            var mock = Activator.CreateInstance(typeof(Mock<>).MakeGenericType(type))!;
            return mock.GetType().GetProperties().Single(property => property.Name == nameof(Mock<object>.Object) && property.PropertyType == type).GetValue(mock)!;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!predicate() && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        predicate().Should().BeTrue("the asynchronous operation should complete before the test timeout");
    }

    private sealed class RecordingSettingsRepository(bool fail = false, TaskCompletionSource? release = null, TimeSpan? delay = null) : ISettingsRepository
    {
        private int activeSaves;
        private int maximumConcurrentSaves;
        public ApplicationSettings? Saved { get; private set; }
        public int SaveCount { get; private set; }
        public int MaximumConcurrentSaves => maximumConcurrentSaves;
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Result<ApplicationSettings>> LoadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<Result> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken)
        {
            SaveCount++;
            var active = Interlocked.Increment(ref activeSaves);
            maximumConcurrentSaves = Math.Max(maximumConcurrentSaves, active);
            SaveStarted.TrySetResult();
            try
            {
                if (release is not null) await release.Task.WaitAsync(cancellationToken);
                if (delay.HasValue) await Task.Delay(delay.Value, cancellationToken);
                if (fail) return Result.Failure(new Error("settings.write_failed", "Mod settings write failed.", ErrorKind.Io));
                Saved = settings;
                return Result.Success();
            }
            finally { Interlocked.Decrement(ref activeSaves); }
        }
    }
}
