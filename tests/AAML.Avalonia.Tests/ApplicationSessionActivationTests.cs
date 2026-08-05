using AAML.Application.Common;
using AAML.Application.Configurations;
using AAML.Application.Diagnostics;
using AAML.Application.Launching;
using AAML.Application.Logging;
using AAML.Application.Mods;
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

namespace AAML.Avalonia.Tests;

[TestClass]
public sealed class ApplicationSessionActivationTests
{
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

    public TestContext TestContext { get; set; }

    private sealed class SessionFixture
    {
        public SessionFixture(bool duplicatePackages = false, bool previewFlow = false)
        {
            First = previewFlow
                ? new ModInstallation(new(ModSource.SteamWorkshop, "C:\\Mods\\first"), new("First"), "First", new WorkshopId(42), false, DescriptorState.Enabled, null,
                    new ModInstallationMetadata("C:\\Mods\\first\\first.XComMod", null, null, [], "C:\\Mods\\first\\local.png", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch))
                : Installation("first", "First");
            Second = Installation("second", duplicatePackages ? "First" : "Second");
            var settings = new ApplicationSettings(ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2WarOfTheChosen,
                "C:\\Game", ["C:\\Mods"], [], [], [], [], CheckForUpdates: false);
            var bootstrapper = new Mock<ISettingsBootstrapper>();
            bootstrapper.Setup(service => service.InitializeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Result<SettingsBootstrapResult>.Success(new(settings, SettingsOrigin.Existing)));
            var catalog = new Mock<IModCatalogSource>();
            catalog.Setup(service => service.DiscoverAsync(It.IsAny<IReadOnlyList<string>>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(Result<IReadOnlyList<ModInstallation>>.Success([First, Second]));
            SettingsRepository = new RecordingSettingsRepository();
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

            var services = new Dictionary<Type, object>
            {
                [typeof(ISettingsBootstrapper)] = bootstrapper.Object, [typeof(IModCatalogSource)] = catalog.Object,
                [typeof(IGameLaunchCoordinator)] = launcher.Object, [typeof(IModIntentService)] = new ModIntentService(SettingsRepository),
                [typeof(IProfileService)] = profiles.Object, [typeof(IModDependencyService)] = dependencies.Object,
                [typeof(IModConflictService)] = conflicts.Object, [typeof(IConfigurationDocumentCatalog)] = documents.Object,
                [typeof(IModDuplicateAnalyzer)] = new ModDuplicateAnalyzer(), [typeof(IApplicationDiagnostics)] = diagnostics.Object,
                [typeof(IWorkshopService)] = workshop.Object, [typeof(IWorkshopPreviewCache)] = previewCache.Object,
                [typeof(IWorkshopOperationCoordinator)] = workshopOperations.Object
            };
            var constructor = typeof(ApplicationSession).GetConstructors().Single();
            Session = (ApplicationSession)constructor.Invoke(constructor.GetParameters().Select(parameter => services.GetValueOrDefault(parameter.ParameterType) ?? MockObject(parameter.ParameterType)).ToArray());
        }

        public ApplicationSession Session { get; }
        public ModInstallation First { get; }
        public ModInstallation Second { get; }
        public RecordingSettingsRepository SettingsRepository { get; }
        public ModProfile? CreatedProfile { get; private set; }
        public GameLaunchRequest? LaunchRequest { get; private set; }

        private static Result<ModConflictReport> EmptyConflicts() => Result<ModConflictReport>.Success(new([], new HashSet<string>()));
        private static ModInstallation Installation(string location, string package) => new(new(ModSource.Manual, location), new(package), package, null, false, DescriptorState.Enabled, null);
        private static object MockObject(Type type)
        {
            var mock = Activator.CreateInstance(typeof(Mock<>).MakeGenericType(type))!;
            return mock.GetType().GetProperties().Single(property => property.Name == nameof(Mock<object>.Object) && property.PropertyType == type).GetValue(mock)!;
        }
    }

    private sealed class RecordingSettingsRepository : ISettingsRepository
    {
        public ApplicationSettings? Saved { get; private set; }
        public Task<Result<ApplicationSettings>> LoadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken) { Saved = settings; return Task.FromResult(Result.Success()); }
    }
}
