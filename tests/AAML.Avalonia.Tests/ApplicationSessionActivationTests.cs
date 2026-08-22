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
using AAML.Application.Mods.Metadata;
using AAML.Application.Mods.Workshop;
using AAML.Application.Ports;
using AAML.Application.Profiles;
using AAML.Application.Settings;
using AAML.Application.Startup;
using AAML.Application.Updates;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Domain.Profiles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Reactive.Bindings;
using System.Diagnostics.CodeAnalysis;
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
    public async Task DependencyMetadataBlock_ProducesBoundedActionableStatusAndDurableDiagnostic()
    {
        var fixture = new SessionFixture(workshopPolicy: WorkshopStartupRefreshPolicy.Manual);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        fixture.Session.SetSelectedActive(new HashSet<ModKey> { fixture.First.Key }, true).IsSuccess.Should().BeTrue();
        var issues = Enumerable.Range(1, 200).Select(index => new ModDependencyIssue(new((ulong)index), new((ulong)index),
            ModDependencyIssueKind.MetadataUnavailable, [new((ulong)index)], $"Steam metadata unavailable for {index}.")).ToArray();
        fixture.DependencyService.Setup(service => service.EvaluateAsync(It.IsAny<IReadOnlyCollection<WorkshopId>>(), It.IsAny<IReadOnlyCollection<WorkshopId>>(), It.IsAny<IReadOnlyCollection<WorkshopId>>(), It.IsAny<IReadOnlyDictionary<WorkshopId, IReadOnlySet<WorkshopId>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<ModDependencyReport>.Success(new(issues, new Dictionary<WorkshopId, IReadOnlyList<WorkshopId>>())));
        fixture.Diagnostics.Invocations.Clear();

        var result = await fixture.Session.LaunchAsync(TestContext.CancellationToken);

        result.Error!.Code.Should().Be("launch.dependencies_blocked");
        fixture.Session.Status.Should().Contain("200 metadata unavailable").And.Contain("Allow launch with missing dependencies");
        fixture.Session.Status.Length.Should().BeLessThan(500);
        fixture.LaunchCoordinator.Verify(service => service.LaunchAsync(It.IsAny<GameLaunchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Diagnostics.Verify(service => service.Write(LocalLogLevel.Warning, "game.launch_blocked", It.IsAny<string>(), It.Is<IReadOnlyDictionary<string, string>>(values => values["blockingIssueCount"] == "200")), Times.Once);
        fixture.Diagnostics.Verify(service => service.FlushAsync(It.IsAny<CancellationToken>()), Times.Once);
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
        using var viewModel = new ModsViewModel(fixture.Session, Mock.Of<IExternalLauncher>(), Mock.Of<IApplicationUiController>());
        var row = fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key);

        viewModel.SetSelection([row]);
        await Task.Delay(50, TestContext.CancellationToken);
        viewModel.SelectedPreviewImagePath.Should().Be("C:\\Cache\\workshop.png");
        viewModel.SetSelection([]);
        viewModel.SelectedPreviewImagePath.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SelectedModCopyActions_UseCurrentVisualOrderAndReportUnavailableValues()
    {
        var fixture = new SessionFixture(previewFlow: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        var copied = new List<string>();
        var ui = new Mock<IApplicationUiController>();
        ui.Setup(service => service.CopyTextAsync(It.IsAny<string>())).Callback<string>(copied.Add).ReturnsAsync(true);
        using var viewModel = new ModsViewModel(fixture.Session, Mock.Of<IExternalLauncher>(), ui.Object);
        var rows = fixture.Session.ModRows.Where(row => row.Key.HasValue).ToArray();
        viewModel.SetSelection([rows[1], rows[0], rows[1]]);

        (await viewModel.CopySelectedNames.Execute().FirstAsync()).IsSuccess.Should().BeTrue();
        copied[^1].Split(Environment.NewLine).Should().Equal(rows.Select(row => row.Name));

        (await viewModel.CopySelectedPaths.Execute().FirstAsync()).IsSuccess.Should().BeTrue();
        copied[^1].Split(Environment.NewLine).Should().Equal(rows.Select(row => row.Location));

        (await viewModel.CopySelectedWorkshopUrls.Execute().FirstAsync()).IsSuccess.Should().BeTrue();
        copied[^1].Should().Contain("https://steamcommunity.com/sharedfiles/filedetails/?id=42")
            .And.Contain("Workshop URL unavailable").And.Contain("has no Workshop ID");

        (await viewModel.CopySelectedReport.Execute().FirstAsync()).IsSuccess.Should().BeTrue();
        copied[^1].Should().Contain("AAML selected mods (2)").And.Contain("Package ID: First").And.Contain("Package ID: Second")
            .And.Contain("Source: SteamWorkshop").And.Contain("Source: Manual").And.Contain("Active: No").And.Contain("Explicit order: Not set");
        viewModel.CopyStatus.Should().Contain("Copied 2 selected mods").And.Contain("deterministic load order").And.Contain("Review local paths");

        var retained = SessionModRow.Retained(new RetainedWorkshopItem(new WorkshopId(99), new PackageId("Missing.Package"), "Missing mod", new ModKey(ModSource.SteamWorkshop, "C:\\Gone\\Missing")));
        SelectedModCopyFormatter.Format([retained], SelectedModCopyFormat.Paths).Should().Be("[Not currently installed: Missing mod; last known path: C:\\Gone\\Missing]");
        viewModel.SetSelection([rows[0]]);
        viewModel.CopyStatus.Should().BeEmpty();

        ui.Setup(service => service.CopyTextAsync(It.IsAny<string>())).ReturnsAsync(false);
        (await viewModel.CopySelectedNames.Execute().FirstAsync()).IsFailure.Should().BeTrue();
        viewModel.CopyStatus.Should().Be("Clipboard is unavailable. Nothing was copied.");

        ui.Setup(service => service.CopyTextAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("clipboard failed"));
        (await viewModel.CopySelectedNames.Execute().FirstAsync()).IsFailure.Should().BeTrue();
        viewModel.CopyStatus.Should().Be("Clipboard is unavailable. Nothing was copied.");
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
        await fixture.PreferencesSaveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
        fixture.PreferencesSaved.Should().NotBeNull();
        var saved = fixture.PreferencesSaved ?? throw new InvalidOperationException("Preferences were not saved.");
        saved.LaunchArguments.Select(argument => argument.Value).Should().Equal("-review");
        saved.AllowLaunchWithMissingDependencies.Should().BeTrue();
        saved.CloseAfterLaunch.Should().BeTrue();
        saved.WorkshopStartupRefresh.Should().Be(WorkshopStartupRefreshPolicy.ActiveMods);
        saved.Theme.Should().Be(ThemePreference.Dark);
        saved.AllowMultipleInstances.Should().BeTrue();
        saved.CheckForUpdates.Should().BeTrue();
        saved.UpdateChannel.Should().Be(UpdateChannelPreference.Prerelease);
        await Task.Delay(500, TestContext.CancellationToken);
        fixture.PreferencesSaveCount.Should().Be(1);
    }

    [TestMethod]
    public async Task DashboardAccessibilitySizing_PreviewsIndependentlyResetsAndDiscardsToPersistedValues()
    {
        var fixture = new SessionFixture(textScale: 1.10m, iconScale: 1.20m);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        var ui = new Mock<IApplicationUiController>();
        using var viewModel = new DashboardViewModel(fixture.Session, ui.Object, PresetService());

        viewModel.TextScale = 1.25m;
        viewModel.IconScale = 0.90m;
        ui.Verify(service => service.ApplyAccessibilitySizing(1.25m, 1.20m), Times.Once);
        ui.Verify(service => service.ApplyAccessibilitySizing(1.25m, 0.90m), Times.Once);

        (await viewModel.ResetAccessibilitySizing.Execute().FirstAsync()).IsSuccess.Should().BeTrue();
        viewModel.TextScale.Should().Be(ApplicationSettingsDefaults.DefaultTextScale);
        viewModel.IconScale.Should().Be(ApplicationSettingsDefaults.DefaultIconScale);
        ui.Verify(service => service.ApplyAccessibilitySizing(ApplicationSettingsDefaults.DefaultTextScale, ApplicationSettingsDefaults.DefaultIconScale), Times.Once);

        (await viewModel.DiscardPreferences.Execute().FirstAsync()).IsSuccess.Should().BeTrue();
        viewModel.TextScale.Should().Be(1.10m);
        viewModel.IconScale.Should().Be(1.20m);
        ui.Verify(service => service.ApplyAccessibilitySizing(1.10m, 1.20m), Times.Once);
        fixture.PreferencesSaveCount.Should().Be(0);
    }

    [TestMethod]
    public async Task DashboardDiscard_JoinsCancelledInFlightAccessibilityAutoSave()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new SessionFixture(autoSave: true, textScale: 1.10m, preferencesSaveRelease: release);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        using var viewModel = new DashboardViewModel(fixture.Session, Mock.Of<IApplicationUiController>(), PresetService());
        viewModel.Activate();
        viewModel.TextScale = 1.40m;
        await fixture.PreferencesSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);

        (await viewModel.DiscardPreferences.Execute().FirstAsync()).IsSuccess.Should().BeTrue();

        viewModel.TextScale.Should().Be(1.10m);
        fixture.PreferencesSaved.Should().BeNull();
        fixture.Session.Settings!.TextScale.Should().Be(1.10m);
    }

    [TestMethod]
    public async Task ModGridDirectPreferences_AutoSaveDebouncesWhileManualSaveViewFlushes()
    {
        var fixture = new SessionFixture(autoSave: true);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        using var viewModel = new ModsViewModel(fixture.Session, Mock.Of<IExternalLauncher>(), Mock.Of<IApplicationUiController>());
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

        await fixture.Session.DiscardModsOwnedDraftsAsync(TestContext.CancellationToken);
        await Task.Delay(550, TestContext.CancellationToken);

        fixture.SettingsRepository.SaveCount.Should().Be(0);
        fixture.Session.HasUnsavedModDrafts.Should().BeFalse();
    }

    [TestMethod]
    public async Task Discard_CancelsInFlightAutoSaveBeforeItCanResurrectDrafts()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new SessionFixture(autoSave: true, modSaveRelease: release);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        fixture.Session.ActivateAutoSaveOwner("mods");
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).IsActive = true;
        await fixture.SettingsRepository.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);

        await fixture.Session.DiscardModsOwnedDraftsAsync(TestContext.CancellationToken);
        await Task.Delay(100, TestContext.CancellationToken);

        fixture.SettingsRepository.Saved.Should().BeNull();
        fixture.Session.HasUnsavedModDrafts.Should().BeFalse();
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).IsActive.Should().BeFalse();
    }

    [TestMethod]
    public async Task Discard_CancelsEveryOverlappingAutoSaveForTheSameOwner()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new SessionFixture(autoSave: true, modSaveRelease: release);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        fixture.Session.ActivateAutoSaveOwner("mods");
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).IsActive = true;
        await fixture.SettingsRepository.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
        fixture.Session.ModRows.Single(item => item.Key == fixture.Second.Key).IsActive = true;
        await Task.Delay(550, TestContext.CancellationToken);

        await fixture.Session.DiscardModsOwnedDraftsAsync(TestContext.CancellationToken);
        release.TrySetResult();
        await Task.Delay(100, TestContext.CancellationToken);

        fixture.SettingsRepository.Saved?.ModIntents.Should().BeNullOrEmpty("a late overlapping completion must never restore discarded activation drafts");
        fixture.Session.HasUnsavedModDrafts.Should().BeFalse();
        fixture.Session.ModRows.Where(item => item.Key.HasValue).Should().OnlyContain(item => item.IsActive == false);
    }

    [TestMethod]
    public async Task ModsDiscard_RestoresEveryOwnedDraftAndCancelsViewSave()
    {
        var persistedGrid = new ModGridPreferences(true, null, true, new HashSet<ModGridGroupKey>());
        var fixture = new SessionFixture(autoSave: true, modGrid: persistedGrid);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        using var viewModel = new ModsViewModel(fixture.Session, Mock.Of<IExternalLauncher>(), Mock.Of<IApplicationUiController>());
        viewModel.Activate();
        fixture.Session.ModRows.Single(row => row.IsGroup).IsExpanded.Should().BeTrue();

        fixture.Session.ModRows.Single(row => row.Key == fixture.First.Key).IsActive = true;
        var groupKey = fixture.Session.ModRows.Single(row => row.IsGroup).GroupKey;
        (await viewModel.ToggleGroup.Execute(groupKey).FirstAsync()).IsSuccess.Should().BeTrue();
        fixture.Session.ModRows.Single(row => row.IsGroup).IsExpanded.Should().BeFalse();
        viewModel.IncludeHidden = false;
        viewModel.SelectedStateFilter = viewModel.StateFilters.Single(option => option.State == ModGridSemanticState.Conflict);
        (await viewModel.DiscardChanges.Execute().FirstAsync()).IsSuccess.Should().BeTrue();
        await Task.Delay(550, TestContext.CancellationToken);

        viewModel.IncludeHidden.Should().BeTrue();
        viewModel.SelectedStateFilter.Should().Be(viewModel.StateFilters.Single(option => option.State is null));
        viewModel.GroupByCategory.Should().BeTrue();
        fixture.Session.ModRows.Single(row => row.IsGroup).IsExpanded.Should().BeTrue();
        fixture.Session.HasUnsavedModDrafts.Should().BeFalse();
        fixture.ModGridSaveCount.Should().Be(0);
    }

    [TestMethod]
    public async Task DashboardDiscard_ReloadsOnlyDashboardAndPreservesModsDraft()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        using var dashboard = new DashboardViewModel(fixture.Session, Mock.Of<IApplicationUiController>(), PresetService());
        dashboard.LaunchArguments = "-unsaved-dashboard";
        fixture.Session.ModRows.Single(row => row.Key == fixture.First.Key).IsActive = true;

        (await dashboard.DiscardPreferences.Execute().FirstAsync()).IsSuccess.Should().BeTrue();

        dashboard.LaunchArguments.Should().BeEmpty();
        fixture.Session.ModRows.Single(row => row.Key == fixture.First.Key).IsActive.Should().BeTrue();
        fixture.Session.HasUnsavedModDrafts.Should().BeTrue();
    }

    [TestMethod]
    public async Task VisibleRefreshCommands_InvokeOnlyTheirOwnedScopesAndUseDraftActivation()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        fixture.Catalog.Invocations.Clear();
        fixture.ConflictService.Invocations.Clear();
        fixture.ConfigurationCatalog.Invocations.Clear();
        fixture.Session.ModRows.Single(row => row.Key == fixture.First.Key).IsActive = true;

        (await fixture.Session.RefreshConflictsAsync(TestContext.CancellationToken)).IsSuccess.Should().BeTrue();

        fixture.Catalog.Verify(service => service.DiscoverAsync(It.IsAny<IReadOnlyList<string>>(), null, It.IsAny<CancellationToken>()), Times.Never);
        fixture.ConfigurationCatalog.Verify(service => service.ListAsync(It.IsAny<IReadOnlyList<ModInstallation>>(), It.IsAny<GameVariant>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.ConflictService.Verify(service => service.AnalyzeAsync(It.IsAny<IReadOnlyList<ModInstallation>>(),
            It.Is<IReadOnlySet<ModKey>>(keys => keys.Contains(fixture.First.Key)), It.IsAny<CancellationToken>()), Times.Once);

        fixture.ConflictService.Invocations.Clear();
        (await fixture.Session.RefreshModsAsync(TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
        fixture.Catalog.Verify(service => service.DiscoverAsync(It.IsAny<IReadOnlyList<string>>(), null, It.IsAny<CancellationToken>()), Times.Once);
        fixture.ConflictService.Verify(service => service.AnalyzeAsync(It.IsAny<IReadOnlyList<ModInstallation>>(),
            It.Is<IReadOnlySet<ModKey>>(keys => keys.Contains(fixture.First.Key)), It.IsAny<CancellationToken>()), Times.Once);
        fixture.ConfigurationCatalog.Verify(service => service.ListAsync(It.IsAny<IReadOnlyList<ModInstallation>>(), It.IsAny<GameVariant>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task StartupAndManualUpdateChecksShareOneSessionOwnedSupportResult()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        using var support = new SupportViewModel(fixture.Session, Mock.Of<IExternalLauncher>(), Mock.Of<IApplicationPaths>(),
            Mock.Of<IApplicationDiagnostics>(), Mock.Of<IApplicationVersionProvider>(), Mock.Of<IApplicationUiController>(), Mock.Of<IGameLogLocator>(), Mock.Of<IGameUserDataLocator>());

        (await fixture.Session.CheckForUpdatesAsync(false, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
        support.UpdateDetails.Should().Contain("Startup Stable update check").And.Contain("up to date");

        (await fixture.Session.CheckForUpdatesAsync(true, TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
        support.UpdateDetails.Should().Contain("Manual Stable update check").And.Contain("up to date");
    }

    [TestMethod]
    public async Task NewerUpdateCheckWinsWhenOlderRequestCompletesLast()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        var older = new TaskCompletionSource<Result<UpdateCheckResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = new TaskCompletionSource<Result<UpdateCheckResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        fixture.UpdateChecks.Setup(service => service.CheckAsync(It.IsAny<UpdateChannelPreference>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref call) == 1 ? older.Task : newer.Task);

        var startup = fixture.Session.CheckForUpdatesAsync(false, TestContext.CancellationToken);
        var manual = fixture.Session.CheckForUpdatesAsync(true, TestContext.CancellationToken);
        newer.SetResult(Result<UpdateCheckResult>.Success(new(UpdateCheckStatus.UpToDate, "1.0.0", null, "newer result")));
        (await manual).IsSuccess.Should().BeTrue();
        older.SetResult(Result<UpdateCheckResult>.Success(new(UpdateCheckStatus.NoEligibleRelease, "1.0.0", null, "older result")));
        (await startup).IsSuccess.Should().BeTrue();

        fixture.Session.UpdateCheck!.Manual.Should().BeTrue();
        fixture.Session.UpdateCheck.Result!.Message.Should().Be("newer result");
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
    public async Task DebouncedWorkerSave_PreservesProjectedRowsAndUsesUiDispatcher()
    {
        var dispatcher = new RecordingUiDispatcher();
        var fixture = new SessionFixture(autoSave: true, uiDispatcher: dispatcher);
        (await fixture.Session.InitializeAsync(TestContext.CancellationToken)).IsSuccess.Should().BeTrue();
        var collectionChanges = 0;
        var firstRow = fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key);
        fixture.Session.ModRows.CollectionChanged += (_, _) =>
        {
            collectionChanges++;
        };
        fixture.Session.ActivateAutoSaveOwner("mods");
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).IsActive = true;

        await WaitUntilAsync(() => fixture.SettingsRepository.SaveCount == 1 && fixture.Session.Status.StartsWith("Saved", StringComparison.Ordinal));

        fixture.Session.ModRows.Should().Contain(item => item.Key == fixture.First.Key && item.IsActive == true);
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).Should().BeSameAs(firstRow);
        fixture.Session.HasUnsavedModDrafts.Should().BeFalse();
        collectionChanges.Should().Be(0, "saving an unchanged keyed projection must not churn the bound collection");
        dispatcher.AsyncInvocations.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public async Task DynamicProjection_PreservesRowsAcrossFilterSortGroupingAndCollapseWithoutReset()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        var first = fixture.Session.ModRows.Single(row => row.Key == fixture.First.Key);
        var second = fixture.Session.ModRows.Single(row => row.Key == fixture.Second.Key);
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        fixture.Session.ModRows.CollectionChanged += (_, args) => actions.Add(args.Action);

        fixture.Session.ProjectMods("First", false);
        fixture.Session.ModRows.Should().Equal(first);
        fixture.Session.ProjectMods(string.Empty, false);
        fixture.Session.ModRows.Single(row => row.Key == fixture.First.Key).Should().BeSameAs(first);
        fixture.Session.ModRows.Single(row => row.Key == fixture.Second.Key).Should().BeSameAs(second);

        second.Order = -1;
        fixture.Session.ModRows.Where(row => row.Key.HasValue).Should().Equal(second, first);
        fixture.Session.SetModGrouping(true);
        var group = fixture.Session.ModRows.Single(row => row.IsGroup);
        var groupKey = group.GroupKey!.Value;
        fixture.Session.ToggleModGroup(groupKey);
        fixture.Session.ModRows.Single(row => row.IsGroup).Should().BeSameAs(group);
        fixture.Session.ModRows.Single(row => row.IsGroup).IsExpanded.Should().BeFalse();
        fixture.Session.ToggleModGroup(groupKey);
        fixture.Session.ModRows.Single(row => row.IsGroup).Should().BeSameAs(group);
        fixture.Session.ModRows.Single(row => row.Key == fixture.First.Key).Should().BeSameAs(first);
        fixture.Session.ModRows.Single(row => row.Key == fixture.Second.Key).Should().BeSameAs(second);

        actions.Should().NotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset);
    }

    [TestMethod]
    public async Task WorkshopProgress_RefreshesOneKeyInPlaceWithoutCollectionReset()
    {
        var fixture = new SessionFixture(previewFlow: true, workshopPolicy: WorkshopStartupRefreshPolicy.Manual);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        var row = fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<WorkshopOperationProgress>? capturedProgress = null;
        WorkshopModState? downloadingState = null;
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        fixture.Session.ModRows.CollectionChanged += (_, args) => actions.Add(args.Action);
        fixture.WorkshopOperations.Setup(service => service.RefreshAsync(It.IsAny<IReadOnlyList<ModInstallation>>(), It.IsAny<IProgress<WorkshopOperationProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<ModInstallation> _, IProgress<WorkshopOperationProgress>? progress, CancellationToken cancellationToken) =>
            {
                var downloading = new WorkshopModState(fixture.First.Key, new WorkshopId(42), UpdateStatus.Downloading, WorkshopItemState.Downloading, null, new(50, 100, 0.5));
                capturedProgress = progress;
                downloadingState = downloading;
                progress!.Report(new("refresh", 0, 1, 50, 100, fixture.First.Key, downloading));
                reported.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                var current = downloading with { Update = UpdateStatus.Current, RawState = WorkshopItemState.Installed, Download = null };
                return new WorkshopBatchResult([new(fixture.First.Key, new WorkshopId(42), current, Result.Success())]);
            });

        var refresh = fixture.Session.RefreshWorkshopStatesAsync(null, TestContext.CancellationToken);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
        await WaitUntilAsync(() => row.IsDownloading && row.DownloadProgress is { } progress && IsApproximately(progress, 50d, 0.1d));

        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).Should().BeSameAs(row);
        actions.Should().BeEmpty();
        release.TrySetResult();
        (await refresh).IsSuccess.Should().BeTrue();
        fixture.Session.ModRows.Single(item => item.Key == fixture.First.Key).Should().BeSameAs(row);
        row.Workshop.Should().Be("Current");
        capturedProgress!.Report(new("refresh", 0, 1, 50, 100, fixture.First.Key, downloadingState));
        await Task.Delay(100, TestContext.CancellationToken);
        row.Workshop.Should().Be("Current", "late progress from a completed generation must be ignored");
        actions.Should().NotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset);
    }

    [TestMethod]
    public async Task WorkerWorkshopRefresh_RaisesBusyNotificationsThroughUiDispatcher()
    {
        var dispatcher = new RecordingUiDispatcher();
        var fixture = new SessionFixture(previewFlow: true, workshopPolicy: WorkshopStartupRefreshPolicy.Manual, uiDispatcher: dispatcher);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        var escaped = false;
        fixture.Session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ApplicationSession.IsWorkshopBusy)) escaped |= !dispatcher.IsInvoking;
        };

        var result = await Task.Run(() => fixture.Session.RefreshWorkshopStatesAsync(null, TestContext.CancellationToken), TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        escaped.Should().BeFalse();
    }

    [TestMethod]
    public async Task ConcurrentDiscoveryRefreshes_AreSerializedAndNewestResultWins()
    {
        var fixture = new SessionFixture();
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        fixture.Catalog.Setup(service => service.DiscoverAsync(It.IsAny<IReadOnlyList<string>>(), null, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref call) == 1)
                {
                    firstStarted.TrySetResult();
                    await firstRelease.Task;
                    return Result<IReadOnlyList<ModInstallation>>.Success([fixture.First]);
                }
                secondStarted.TrySetResult();
                return Result<IReadOnlyList<ModInstallation>>.Success([fixture.Second]);
            });

        var firstRefresh = fixture.Session.RefreshModsAsync(TestContext.CancellationToken);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.CancellationToken);
        var secondRefresh = fixture.Session.RefreshModsAsync(TestContext.CancellationToken);
        await Task.Delay(100, TestContext.CancellationToken);
        secondStarted.Task.IsCompleted.Should().BeFalse("the second catalog call must wait for the first refresh transaction");
        firstRelease.TrySetResult();

        (await firstRefresh).IsSuccess.Should().BeTrue();
        (await secondRefresh).IsSuccess.Should().BeTrue();
        fixture.Session.DiscoveredMods.Should().Equal(fixture.Second);
        fixture.Session.ModRows.Where(row => row.Key.HasValue).Should().ContainSingle(row => row.Key == fixture.Second.Key);
    }

    [TestMethod]
    public async Task WorkerDiscovery_AppliesBoundStateOnlyThroughUiDispatcher()
    {
        var dispatcher = new RecordingUiDispatcher();
        var fixture = new SessionFixture(uiDispatcher: dispatcher);
        await fixture.Session.InitializeAsync(TestContext.CancellationToken);
        var escaped = false;
        fixture.Session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ApplicationSession.DiscoveredMods)) escaped |= !dispatcher.IsInvoking;
        };
        fixture.Session.ModRows.CollectionChanged += (_, _) => escaped |= !dispatcher.IsInvoking;

        var result = await Task.Run(() => fixture.Session.RefreshModsAsync(TestContext.CancellationToken), TestContext.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        escaped.Should().BeFalse();
        dispatcher.AsyncInvocations.Should().BeGreaterThan(0);
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
        [SuppressMessage("Major Code Smell", "S107", Justification = "This construction pattern matches the existing suite factory API and maintains the test harness behavior.")]
        public SessionFixture(bool duplicatePackages = false, bool previewFlow = false, WorkshopStartupRefreshPolicy workshopPolicy = WorkshopStartupRefreshPolicy.AllMods, bool navigationRailSaveFails = false, NavigationRailMode navigationRailMode = NavigationRailMode.Expanded, bool delayDiscovery = false, bool autoSave = false, bool modSaveFails = false, TaskCompletionSource? modSaveRelease = null, TimeSpan? modSaveDelay = null, IUiDispatcher? uiDispatcher = null, ModGridPreferences? modGrid = null, decimal textScale = ApplicationSettingsDefaults.DefaultTextScale, decimal iconScale = ApplicationSettingsDefaults.DefaultIconScale, TaskCompletionSource? preferencesSaveRelease = null)
        {
            First = previewFlow
                ? new ModInstallation(new(ModSource.SteamWorkshop, "C:\\Mods\\first"), new("First"), "First", new WorkshopId(42), false, DescriptorState.Enabled, null,
                    new ModInstallationMetadata("C:\\Mods\\first\\first.XComMod", null, null, [], "C:\\Mods\\first\\local.png", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
                : Installation("first", "First");
            Second = Installation("second", duplicatePackages ? "First" : "Second");
            InitialSettings = CreateSettings(navigationRailMode, workshopPolicy) with { AutoSaveChanges = autoSave, ModGrid = modGrid ?? ModGridPreferences.Default, TextScale = textScale, IconScale = iconScale };
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
            Bootstrapper.Setup(service => service.SavePreferencesAsync(It.IsAny<ApplicationSettings>(), It.IsAny<IReadOnlyList<LaunchArgument>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<WorkshopStartupRefreshPolicy>(), It.IsAny<ThemePreference>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<UpdateChannelPreference>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                .Returns((IInvocation invocation) =>
                {
                    var request = new PreferencesSaveRequest(
                        (ApplicationSettings)invocation.Arguments[0],
                        (IReadOnlyList<LaunchArgument>)invocation.Arguments[1],
                        (IReadOnlyList<string>)invocation.Arguments[2],
                        (bool)invocation.Arguments[3],
                        (bool)invocation.Arguments[4],
                        (WorkshopStartupRefreshPolicy)invocation.Arguments[5],
                        (ThemePreference)invocation.Arguments[6],
                        (bool)invocation.Arguments[7],
                        (bool)invocation.Arguments[8],
                        (UpdateChannelPreference)invocation.Arguments[9],
                        (decimal)invocation.Arguments[10],
                        (decimal)invocation.Arguments[11],
                        (CancellationToken)invocation.Arguments[12]);
                    return SavePreferencesAsync(request, preferencesSaveRelease);
                });
            Bootstrapper.Setup(service => service.SaveModGridPreferencesAsync(It.IsAny<ApplicationSettings>(), It.IsAny<ModGridPreferences>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ApplicationSettings current, ModGridPreferences grid, CancellationToken _) =>
                {
                    ModGridSaveCount++;
                    ModGridSaved = grid;
                    return Result<ApplicationSettings>.Success(current with { ModGrid = grid });
                });
            Catalog = new Mock<IModCatalogSource>();
            Catalog.Setup(service => service.DiscoverAsync(It.IsAny<IReadOnlyList<string>>(), null, It.IsAny<CancellationToken>())).Returns(async () =>
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
            DependencyService = new Mock<IModDependencyService>();
            DependencyService.Setup(service => service.EvaluateAsync(It.IsAny<IReadOnlyCollection<WorkshopId>>(), It.IsAny<IReadOnlyCollection<WorkshopId>>(), It.IsAny<IReadOnlyCollection<WorkshopId>>(), It.IsAny<IReadOnlyDictionary<WorkshopId, IReadOnlySet<WorkshopId>>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<ModDependencyReport>.Success(new([], new Dictionary<WorkshopId, IReadOnlyList<WorkshopId>>())));
            ConflictService = new Mock<IModConflictService>();
            ConflictService.Setup(service => service.AnalyzeAsync(It.IsAny<IReadOnlyList<ModInstallation>>(), It.IsAny<IReadOnlySet<ModKey>>(), It.IsAny<CancellationToken>())).ReturnsAsync(EmptyConflicts());
            ConflictService.Setup(service => service.SetActiveAsync(It.IsAny<IReadOnlySet<ModKey>>(), It.IsAny<CancellationToken>())).ReturnsAsync(EmptyConflicts());
            ConfigurationCatalog = new Mock<IConfigurationDocumentCatalog>();
            ConfigurationCatalog.Setup(service => service.ListAsync(It.IsAny<IReadOnlyList<ModInstallation>>(), It.IsAny<GameVariant>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result<IReadOnlyList<ConfigurationDocumentSummary>>.Success([]));
            UpdateChecks = new Mock<IUpdateCheckService>();
            UpdateChecks.Setup(service => service.CheckAsync(It.IsAny<UpdateChannelPreference>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<UpdateCheckResult>.Success(new(UpdateCheckStatus.UpToDate, "1.0.0", null, "AAML is up to date.")));
            LaunchCoordinator = new Mock<IGameLaunchCoordinator>();
            LaunchCoordinator.Setup(service => service.LaunchAsync(It.IsAny<GameLaunchRequest>(), It.IsAny<CancellationToken>())).Callback<GameLaunchRequest, CancellationToken>((request, _) => LaunchRequest = request)
                .ReturnsAsync(Result<GameLaunchOutcome>.Success(new(null, new(DateTimeOffset.UtcNow, 42, "game"))));
            Diagnostics = new Mock<IApplicationDiagnostics>();
            Diagnostics.Setup(service => service.FlushAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success());
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

            var subscribe = new Mock<IWorkshopSubscriptionCoordinator>();
            var removal = new Mock<IModRemovalFilesystem>();
            var duplicatePrefs = new Mock<IDuplicatePreferenceService>();
            var gameWriter = new Mock<IGameConfigurationWriter>();
            var steamSettings = new Mock<ISteamSettingsIntegrator>();
            var profileInterchange = new Mock<IProfileInterchange>();
            var legacyImport = new Mock<ILegacyProfileImportService>();
            var modDuplicateAnalyzer = new ModDuplicateAnalyzer();
            var modMetadata = new Mock<IModMetadataService>();
            var services = new ServiceCollection();
            services.AddSingleton<ISettingsBootstrapper>(Bootstrapper.Object);
            services.AddSingleton<IModCatalogSource>(Catalog.Object);
            services.AddSingleton<IGameLaunchCoordinator>(LaunchCoordinator.Object);
            services.AddSingleton<IGameConfigurationWriter>(gameWriter.Object);
            services.AddSingleton<ISteamSettingsIntegrator>(steamSettings.Object);
            services.AddSingleton<IModIntentService>(new ModIntentService(SettingsRepository));
            services.AddSingleton<IProfileService>(profiles.Object);
            services.AddSingleton<IProfileInterchange>(profileInterchange.Object);
            services.AddSingleton<ILegacyProfileImportService>(legacyImport.Object);
            services.AddSingleton<IModDependencyService>(DependencyService.Object);
            services.AddSingleton<IModMetadataService>(modMetadata.Object);
            services.AddSingleton<IModConflictService>(ConflictService.Object);
            services.AddSingleton<IConfigurationDocumentCatalog>(ConfigurationCatalog.Object);
            services.AddSingleton<IWorkshopOperationCoordinator>(workshopOperations.Object);
            services.AddSingleton<IWorkshopSubscriptionCoordinator>(subscribe.Object);
            services.AddSingleton<IModRemovalFilesystem>(removal.Object);
            services.AddSingleton<IModDuplicateAnalyzer>(modDuplicateAnalyzer);
            services.AddSingleton<IDuplicatePreferenceService>(duplicatePrefs.Object);
            services.AddSingleton<IWorkshopService>(workshop.Object);
            services.AddSingleton<IWorkshopPreviewCache>(previewCache.Object);
            services.AddSingleton<IUpdateCheckService>(UpdateChecks.Object);
            services.AddSingleton<IApplicationDiagnostics>(Diagnostics.Object);
            services.AddSingleton<IExistingModRootPreviewGuard>(RootGuard);
            services.AddSingleton<IUiDispatcher>(uiDispatcher ?? new InlineUiDispatcher());
            var provider = services.BuildServiceProvider();
            Session = new ApplicationSession(provider);
        }

        public ApplicationSession Session { get; }
        public ApplicationSettings InitialSettings { get; }
        public Mock<ISettingsBootstrapper> Bootstrapper { get; }
        public TaskCompletionSource DiscoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModInstallation First { get; }
        public ModInstallation Second { get; }
        public RecordingSettingsRepository SettingsRepository { get; }
        public Mock<IWorkshopOperationCoordinator> WorkshopOperations { get; }
        public Mock<IModCatalogSource> Catalog { get; }
        public Mock<IModConflictService> ConflictService { get; }
        public Mock<IConfigurationDocumentCatalog> ConfigurationCatalog { get; }
        public Mock<IUpdateCheckService> UpdateChecks { get; }
        public Mock<IModDependencyService> DependencyService { get; }
        public Mock<IGameLaunchCoordinator> LaunchCoordinator { get; }
        public Mock<IApplicationDiagnostics> Diagnostics { get; }
        public ExistingModRootPreviewGuard RootGuard { get; }
        public ModProfile? CreatedProfile { get; private set; }
        public GameLaunchRequest? LaunchRequest { get; private set; }
        public ApplicationSettings? NavigationRailSaved { get; private set; }
        public bool? AutoSavePreferenceSaved { get; private set; }
        public int PreferencesSaveCount { get; private set; }
        public ApplicationSettings? PreferencesSaved { get; private set; }
        public TaskCompletionSource PreferencesSaveCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PreferencesSaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ModGridSaveCount { get; private set; }
        public ModGridPreferences? ModGridSaved { get; private set; }

        public void ReleaseDiscovery() => discoveryRelease.TrySetResult();

        public static ApplicationSettings CreateSettings(NavigationRailMode navigationRailMode, WorkshopStartupRefreshPolicy workshopPolicy = WorkshopStartupRefreshPolicy.AllMods) =>
            new(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2WarOfTheChosen,
                "C:\\Game", ["C:\\Mods"], [], [], [], [], WorkshopStartupRefresh: workshopPolicy, CheckForUpdates: false, NavigationRailMode: navigationRailMode);

        private async Task<Result<ApplicationSettings>> SavePreferencesAsync(PreferencesSaveRequest request, TaskCompletionSource? preferencesSaveRelease)
        {
            PreferencesSaveStarted.TrySetResult();
            if (preferencesSaveRelease is not null) await preferencesSaveRelease.Task.WaitAsync(request.CancellationToken);
            PreferencesSaveCount++;
            PreferencesSaved = request.Current with
            {
                LaunchArguments = request.Arguments,
                ModRootLocations = request.Roots,
                AllowLaunchWithMissingDependencies = request.AllowMissing,
                CloseAfterLaunch = request.CloseAfter,
                WorkshopStartupRefresh = request.Workshop,
                Theme = request.Theme,
                AllowMultipleInstances = request.Multiple,
                CheckForUpdates = request.CheckUpdates,
                UpdateChannel = request.Channel,
                TextScale = request.TextScale,
                IconScale = request.IconScale,
            };
            PreferencesSaveCompleted.TrySetResult();
            return Result<ApplicationSettings>.Success(PreferencesSaved);
        }

        private readonly TaskCompletionSource discoveryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed record PreferencesSaveRequest(
            ApplicationSettings Current,
            IReadOnlyList<LaunchArgument> Arguments,
            IReadOnlyList<string> Roots,
            bool AllowMissing,
            bool CloseAfter,
            WorkshopStartupRefreshPolicy Workshop,
            ThemePreference Theme,
            bool Multiple,
            bool CheckUpdates,
            UpdateChannelPreference Channel,
            decimal TextScale,
            decimal IconScale,
            CancellationToken CancellationToken);

        private sealed class InlineUiDispatcher : IUiDispatcher
        {
            public void Invoke(Action action) => action();
            public Task InvokeAsync(Action action, CancellationToken cancellationToken) { action(); return Task.CompletedTask; }
            public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken) => Task.FromResult(action());
        }

        private static Result<ModConflictReport> EmptyConflicts() => Result<ModConflictReport>.Success(new([], new HashSet<string>()));
        private static ModInstallation Installation(string location, string package) => new(new(ModSource.Manual, location), new(package), package, null, false, DescriptorState.Enabled, null);
    }

    private static bool IsApproximately(double value, double target, double tolerance) => Math.Abs(value - target) <= tolerance;

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!predicate() && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        predicate().Should().BeTrue("the asynchronous operation should complete before the test timeout");
    }

    private sealed class RecordingUiDispatcher : IUiDispatcher
    {
        private int invokeDepth;
        private int asyncInvocations;

        public bool IsInvoking => Volatile.Read(ref invokeDepth) > 0;
        public int AsyncInvocations => Volatile.Read(ref asyncInvocations);

        public void Invoke(Action action)
        {
            Interlocked.Increment(ref invokeDepth);
            try { action(); }
            finally { Interlocked.Decrement(ref invokeDepth); }
        }

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref asyncInvocations);
            Invoke(action);
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref asyncInvocations);
            T? result = default;
            Invoke(() => result = action());
            return Task.FromResult(result!);
        }
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
