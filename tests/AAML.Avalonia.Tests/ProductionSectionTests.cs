using System.Reflection;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Headless;
using CSharpFunctionalExtensions;
using FluentAssertions;
using Moq;
using ReactiveUI;
using ReactiveUI.Avalonia;
using AAML.Application;
using AAML.Application.Diagnostics;
using AAML.Application.Ports;

namespace AAML.Avalonia.Tests;

[TestClass]
public sealed class ProductionSectionTests
{
    private static readonly Type[] ViewModelTypes =
    [
        typeof(DashboardViewModel), typeof(ModsViewModel), typeof(ConflictsViewModel),
        typeof(ConfigurationsViewModel), typeof(ProfilesViewModel), typeof(MigrationViewModel),
        typeof(SupportViewModel), typeof(ModCleanupViewModel)
    ];

    private static readonly Type[] ViewTypes =
    [
        typeof(DashboardView), typeof(ModsView), typeof(ConflictsView), typeof(ConfigurationsView),
        typeof(ProfilesView), typeof(MigrationView), typeof(SupportView), typeof(ModCleanupView)
    ];

    [ClassInitialize]
    public static void InitializeAvalonia(TestContext _)
    {
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).UseReactiveUI(_ => { }).SetupWithoutStarting();
    }

    [TestMethod]
    public void SectionsHaveUniqueRoutesAndContiguousOrders()
    {
        App.SectionTypes.Should().Equal(ViewModelTypes);
        var sections = ViewModelTypes.Select(ReadSection).OrderBy(section => section.Order).ToArray();

        sections.Select(section => section.Route).Should().Equal(
            "dashboard", "mods", "conflicts", "configurations", "profiles", "migration", "support", "cleanup");
        sections.Select(section => section.Route).Should().OnlyHaveUniqueItems();
        sections.Select(section => section.Order).Should().Equal(Enumerable.Range(0, 8));
    }

    [TestMethod]
    public void EveryRegisteredViewConstructsAndLoadsItsAxaml()
    {
        foreach (var viewType in ViewTypes)
        {
            var view = Activator.CreateInstance(viewType);
            view.Should().NotBeNull($"{viewType.Name} must load its production AXAML");
        }
    }

    [TestMethod]
    public void CurrentProductSurfaceOmitsRetiredUiAuditMembers()
    {
        typeof(DashboardViewModel).GetProperty("SaveLaunchPolicy").Should().BeNull();
        typeof(DashboardViewModel).GetProperty("GamePath").Should().BeNull();
        typeof(ModsViewModel).GetProperty("ToggleInspector").Should().BeNull();
        typeof(SessionModRow).GetProperty("Compatibility").Should().BeNull();
        typeof(SessionModRow).GetProperty("HasPreviewImage").Should().BeNull();
        typeof(SessionDependencyRelationship).GetProperty("PathText").Should().BeNull();
        typeof(ApplicationSession).GetProperty("WorkshopAggregateProgress").Should().BeNull();
        typeof(ApplicationSession).GetMethod("SetAllowLaunchWithMissingDependenciesAsync").Should().BeNull();
        typeof(ApplicationSession).GetMethods().Should().NotContain(method =>
            method.Name == "ImportLegacyProfileAsync" &&
            method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(string), typeof(string), typeof(CancellationToken) }));
        typeof(SessionModMetadata).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name)
            .Should().NotContain("InstalledName");
    }

    [TestMethod]
    public void EverySectionViewModelExposesConstructedCommands()
    {
        foreach (var viewModelType in ViewModelTypes.Where(type => type != typeof(SupportViewModel)))
        {
            var viewModel = CreateViewModel(viewModelType);
            var commands = viewModelType.GetProperties().Where(property => property.PropertyType.Name.StartsWith("IEnhancedCommand", StringComparison.Ordinal)).ToArray();

            commands.Should().NotBeEmpty($"{viewModelType.Name} must expose user actions");
            commands.Select(command => command.GetValue(viewModel)).Should().NotContainNulls();
            (viewModel as IDisposable)?.Dispose();
        }
    }

    [TestMethod]
    public async Task MigrationApplyCommandsRejectConfirmationWithoutPreview()
    {
        var viewModel = (MigrationViewModel)CreateViewModel(typeof(MigrationViewModel));

        (await Execute(viewModel.ApplyActiveMods)).Error.Should().Be("Preview active mods before applying.");
        (await Execute(viewModel.ApplySnapshots)).Error.Should().Be("Preview legacy snapshots before applying.");
        (await Execute(viewModel.ApplyOverrideCleanup)).Error.Should().Be("Preview override cleanup before applying.");
    }

    [TestMethod]
    public async Task CleanupConfirmationRejectsWithoutPreview()
    {
        using var viewModel = (ModCleanupViewModel)CreateViewModel(typeof(ModCleanupViewModel));

        var result = await Execute(viewModel.Confirm);

        result.Error.Should().Be("Preview cleanup before confirming.");
    }

    [TestMethod]
    public async Task EmptyProfileAndSettingsActionsFailSafely()
    {
        var profiles = (ProfilesViewModel)CreateViewModel(typeof(ProfilesViewModel));
        var dashboard = (DashboardViewModel)CreateViewModel(typeof(DashboardViewModel));

        (await Execute(profiles.Create)).IsFailure.Should().BeTrue();
        dashboard.GameInstallationPath = "";
        (await Execute(dashboard.SaveInstallation)).IsFailure.Should().BeTrue();
    }

    [TestMethod]
    public async Task SupportMetadataAndLinksAreOwnedByAamlRepository()
    {
        var launcher = new Mock<IExternalLauncher>();
        var opened = new List<Uri>();
        launcher.Setup(port => port.OpenUriAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .Callback<Uri, CancellationToken>((uri, _) => opened.Add(uri))
            .ReturnsAsync(AAML.Application.Common.Result.Success());
        var versions = new Mock<IApplicationVersionProvider>();
        versions.Setup(port => port.GetCurrentVersion()).Returns(AAML.Application.Common.Result<string>.Success("1.2.3"));
        var viewModel = (SupportViewModel)CreateViewModel(typeof(SupportViewModel), launcher.Object, versions.Object);

        await Execute(viewModel.OpenProject);
        await Execute(viewModel.OpenIssues);
        await Execute(viewModel.OpenWiki);

        viewModel.Product.Should().Be("Avalonia Alternative Mod Launcher");
        viewModel.Version.Should().Be("1.2.3");
        opened.Should().Equal(ProjectIdentity.RepositoryUri, ProjectIdentity.IssuesUri, ProjectIdentity.WikiUri);
        opened.Should().OnlyContain(uri => uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/JakeRoxs/xcom2-dark-launcher", StringComparison.Ordinal));
    }

    private static object CreateViewModel(Type type, params object[] overrides)
    {
        var session = CreateSession();
        var constructor = type.GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter =>
            parameter.ParameterType == typeof(ApplicationSession)
                ? session
                : overrides.FirstOrDefault(value => parameter.ParameterType.IsInstanceOfType(value)) ?? CreateMock(parameter.ParameterType)).ToArray();
        return constructor.Invoke(arguments);
    }

    private static ApplicationSession CreateSession()
    {
        var constructor = typeof(ApplicationSession).GetConstructors().Single();
        return (ApplicationSession)constructor.Invoke(constructor.GetParameters().Select(parameter => CreateMock(parameter.ParameterType)).ToArray());
    }

    private static object CreateMock(Type type)
    {
        var mock = Activator.CreateInstance(typeof(Mock<>).MakeGenericType(type))!;
        return mock.GetType().GetProperties().Single(property => property.Name == nameof(Mock<object>.Object) && property.PropertyType == type).GetValue(mock)!;
    }

    private static async Task<Result> Execute(Zafiro.UI.Commands.IEnhancedCommand<Result> command) => await command.Execute().FirstAsync();

    private static (string Route, int Order) ReadSection(Type type)
    {
        var attribute = type.CustomAttributes.Single(item => item.AttributeType.Name == "SectionAttribute");
        return ((string)attribute.ConstructorArguments[0].Value!, (int)attribute.ConstructorArguments[2].Value!);
    }
}
