using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.ApplicationLifetimes;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Platform;
using global::Avalonia.Automation;
using global::Avalonia.VisualTree;
using AAML.Application.Logging;
using AAML.Application.Launching;
using AAML.Application.Mods;
using AAML.Application.Mods.Dependencies;
using AAML.Application.Mods.Metadata;
using AAML.Application.Mods.Conflicts;
using AAML.Application.Mods.Workshop;
using AAML.Application.Mods.Duplicates;
using AAML.Application.Configurations;
using AAML.Application.Profiles;
using AAML.Application.Ports;
using AAML.Application.Startup;
using AAML.Infrastructure.Common.Logging;
using AAML.Infrastructure.Common.Mods;
using AAML.Infrastructure.Common.Profiles;
using AAML.Infrastructure.Common.Settings;
using AAML.Infrastructure.Common.Steam;
using AAML.Infrastructure.Common.Files;
using AAML.Infrastructure.Common.Configurations;
using AAML.Infrastructure.Common.Compatibility.Profiles;
using AAML.Infrastructure.Linux.Paths;
using AAML.Infrastructure.Steam;
using AAML.Infrastructure.Windows.Paths;
using AAML.Infrastructure.Windows.Processes;
using AAML.Infrastructure.Windows.Launching;
using AAML.Infrastructure.Windows.Steam;
using AAML.Infrastructure.Linux.Steam;
using AAML.Infrastructure.Linux.Launching;
using AAML.Infrastructure.Linux.Processes;
using AAML.Application.Steam;
using AAML.Application.Diagnostics;
using AAML.Application.Updates;
using AAML.Application.Mods.Cleanup;
using AAML.Infrastructure.Common.Updates;
using Zafiro.Avalonia.Controls;
using Zafiro.UI.Navigation.Sections;
using AAML.Infrastructure.Common.Startup;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Zafiro.Avalonia.Controls.Shell;
using Zafiro.Avalonia.Dialogs;
using Zafiro.Avalonia.Dialogs.Implementations;
using Zafiro.Avalonia.Icons;
using Zafiro.UI;
using Zafiro.UI.Navigation;
using Zafiro.UI.Shell;

namespace AAML.Avalonia;

public sealed partial class App : global::Avalonia.Application
{
    internal static IReadOnlyList<Type> SectionTypes { get; } =
    [
        typeof(DashboardViewModel), typeof(ModsViewModel), typeof(ConflictsViewModel), typeof(ConfigurationsViewModel),
        typeof(ProfilesViewModel), typeof(MigrationViewModel), typeof(SupportViewModel), typeof(ModCleanupViewModel)
    ];

    private ServiceProvider? provider;
    private Mutex? singleInstanceMutex;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        IconControlProviderRegistry.Register(new OptrisIconControlProvider(), asDefault: true);
        var shellLogger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
        var services = new ServiceCollection();
        RegisterPlatform(services);
        services.AddZafiroShell(logger: shellLogger);
        services.AddSectionsFromAttributes(typeof(DashboardViewModel).Assembly, SectionTypes.Contains, shellLogger);
        var dialog = DialogService.Create();
        services.AddSingleton(dialog);
        services.AddSingleton<INotificationService>(new NotificationDialog(dialog));
        services.AddSingleton<IProfileDocumentTransfer>(_ => new AvaloniaProfileDocumentTransfer(() => (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow));
        services.AddSingleton<IApplicationUiController, ApplicationUiController>();
        services.AddSingleton<ILaunchArgumentPresetService, LaunchArgumentPresetService>();
        services.AddSingleton<ApplicationSession>();

        provider = services.BuildServiceProvider();
        FatalErrorCoordinator.Configure(provider.GetRequiredService<IApplicationDiagnostics>(), provider.GetRequiredService<IExternalLauncher>(), provider.GetRequiredService<IApplicationUiController>());
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = provider.GetRequiredService<IHierarchicalShell>();
            var session = provider.GetRequiredService<ApplicationSession>();
            var startupSettings = ShellStartupSettings.LoadAsync(provider.GetRequiredService<ISettingsRepository>(), CancellationToken.None).GetAwaiter().GetResult();
            if (startupSettings is not null) session.PrimeSettings(startupSettings);
            // A failed or missing preflight load is retried once by full initialization after the Expanded shell is visible.
            var shellView = new AamlShellView(startupSettings?.NavigationRailMode ?? AAML.Application.Settings.NavigationRailMode.Expanded) { DataContext = shell };
            if (startupSettings is not null) shellView.Configure(session);
            desktop.MainWindow = new Window
            {
                Title = "Avalonia Alternative Mod Launcher",
                Icon = CreateWindowIcon(),
                Width = 1180,
                Height = 760,
                MinWidth = 820,
                MinHeight = 560,
                Content = shellView
            };
            shellView.LayoutUpdated += (_, _) => AssignShellNavigationAutomationIds(shellView);
            desktop.MainWindow.Opened += async (_, _) =>
            {
                var initialized = await session.InitializeAsync(CancellationToken.None);
                if (!initialized.IsSuccess) return;
                shellView.Configure(session);
                provider.GetRequiredService<IApplicationUiController>().ApplyTheme(session.Settings!.Theme);
                if (!session.Settings.AllowMultipleInstances)
                {
                    singleInstanceMutex = new Mutex(true, "AAML.Avalonia.SingleInstance", out var created);
                    if (!created) desktop.Shutdown();
                }
            };
            desktop.Exit += async (_, _) =>
            {
                if (provider is not null) await provider.DisposeAsync();
                if (singleInstanceMutex is not null) { try { singleInstanceMutex.ReleaseMutex(); } catch (ApplicationException) { } singleInstanceMutex.Dispose(); }
                await shellLogger.DisposeAsync();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    internal static WindowIcon CreateWindowIcon() => new(AssetLoader.Open(new Uri("avares://AAML.Avalonia/Assets/aaml-icon.png")));

    private static void AssignShellNavigationAutomationIds(AamlShellView shellView)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dashboard"] = "ShellSectionDashboard",
            ["mods"] = "ShellSectionMods",
            ["conflicts"] = "ShellSectionConflicts",
            ["configurations"] = "ShellSectionConfigurations",
            ["profiles"] = "ShellSectionProfiles",
            ["migration"] = "ShellSectionMigration",
            ["support"] = "ShellSectionSupport",
            ["cleanup"] = "ShellSectionCleanup"
        };

        foreach (var item in shellView.GetVisualDescendants().OfType<SectionStripItem>())
        {
            if (item.DataContext is not ISection section || !sections.TryGetValue(section.Id, out var automationId)) continue;
            AutomationProperties.SetAutomationId(item, automationId);
            AutomationProperties.SetName(item, section.FriendlyName);
            AutomationProperties.SetHelpText(item, $"Navigate to {section.FriendlyName}");
            ToolTip.SetTip(item, section.FriendlyName);
        }
    }

    private static void RegisterPlatform(IServiceCollection services)
    {
        IApplicationPaths paths;
        IApplicationPaths formerPaths;
        IPathSemantics semantics;
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            paths = new WindowsApplicationPaths(localAppData);
            formerPaths = new WindowsApplicationPaths(localAppData, "XCOM2 Alternative Mod Launcher");
            semantics = new WindowsPathSemantics();
            services.AddSingleton<IProcessRunner, WindowsProcessRunner>();
            services.AddSingleton<IExternalLauncher, WindowsExternalLauncher>();
            services.AddSingleton<IGameLogLocator, WindowsGameLogLocator>();
            services.AddSingleton<IGameConfigurationWriter, WindowsGameConfigurationWriter>();
            services.AddSingleton<ILegacyGameConfigurationSource, WindowsLegacyGameConfigurationSource>();
            services.AddSingleton<IGameLauncher, WindowsGameLauncher>();
            services.AddSingleton<ISteamFilesystemDiscovery, WindowsSteamFilesystemDiscovery>();
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? throw new InvalidOperationException("HOME is required on Linux.");
            var options = new LinuxApplicationPathOptions(
                home,
                Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
                Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
                Environment.GetEnvironmentVariable("XDG_STATE_HOME"),
                Environment.GetEnvironmentVariable("XDG_CACHE_HOME"),
                Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
                $"/tmp/aaml-{Environment.UserName}");
            paths = new LinuxApplicationPaths(options);
            formerPaths = new LinuxApplicationPaths(options with { RuntimeFallbackDirectory = $"/tmp/xcom2-alternative-mod-launcher-{Environment.UserName}" }, "xcom2-alternative-mod-launcher");
            semantics = new LinuxPathSemantics();
            services.AddSingleton<ISteamFilesystemDiscovery>(_ => new LinuxSteamFilesystemDiscovery(new LinuxPhysicalPathResolver()));
            var requestStore = new LinuxSteamLaunchRequestStore(paths.RuntimeDirectory);
            services.AddSingleton<ISteamLaunchRequestStore>(requestStore);
            services.AddSingleton<IGameConfigurationWriter, LinuxGameConfigurationWriter>();
            services.AddSingleton<ILegacyGameConfigurationSource, LinuxLegacyGameConfigurationSource>();
            services.AddSingleton<IExternalLauncher, LinuxExternalLauncher>();
            services.AddSingleton<IGameLogLocator, UnavailableGameLogLocator>();
            services.AddSingleton<IGameLauncher>(_ => new LinuxSteamGameLauncher(requestStore));
        }
        else throw new PlatformNotSupportedException("AAML currently supports Windows and native Linux.");

        var migration = ModernDataRootMigrator.Migrate(formerPaths, paths, CancellationToken.None);
        if (migration.Status == DataRootMigrationStatus.Failed) throw new InvalidOperationException("AAML could not safely migrate durable application data to its new storage root.");

        services.AddSingleton<IApplicationPaths>(paths);
        services.AddSingleton<IPathSemantics>(semantics);
        services.AddSingleton<ISettingsRepository, JsonSettingsRepository>();
        services.AddSingleton<ILegacyLaunchArgumentSuggestionRepository>(_ => new LegacyLaunchArgumentSuggestionRepository(
            Path.Combine(paths.ConfigurationDirectory, "legacy-migration-v1.json")));
        services.AddSingleton<IAtomicTextWriter, AtomicTextWriter>();
        services.AddSingleton<ILegacySettingsImporter>(_ => new LegacySettingsFileImporter(
            [Path.Combine(AppContext.BaseDirectory, "settings.json")],
            (_, path) => new WindowsPathSemantics().NormalizeIdentity(path),
            Path.Combine(paths.ConfigurationDirectory, "legacy-migration-v1.json")));
        services.AddSingleton<ISettingsBootstrapper, SettingsBootstrapper>();
        services.AddSingleton<ISteamSettingsIntegrator, SteamSettingsIntegrator>();
        services.AddSingleton<IModCatalogSource, FilesystemModCatalogSource>();
        services.AddSingleton<IModIntentService, ModIntentService>();
        services.AddSingleton<IModDependencyService, ModDependencyService>();
        services.AddSingleton<IModMetadataService, ModMetadataService>();
        services.AddSingleton<IModContentIndexer, FilesystemModContentIndexer>();
        services.AddSingleton<IModConflictService, ModConflictService>();
        services.AddSingleton<IWorkshopOperationCoordinator, WorkshopOperationCoordinator>();
        services.AddSingleton<IWorkshopSubscriptionCoordinator, WorkshopSubscriptionCoordinator>();
        services.AddSingleton<IModRemovalFilesystem, SafeModRemovalFilesystem>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IModCleanupService, SafeModCleanupService>();
        services.AddSingleton<IModDuplicateAnalyzer, ModDuplicateAnalyzer>();
        services.AddSingleton<IDuplicatePreferenceService, DuplicatePreferenceService>();
        services.AddSingleton<IConfigurationDocumentCatalog, FilesystemConfigurationDocumentCatalog>();
        services.AddSingleton<IConfigurationFileRepository, FilesystemConfigurationFileRepository>();
        services.AddSingleton<IConfigurationSnapshotRepository, JsonConfigurationSnapshotRepository>();
        services.AddSingleton<ILegacySnapshotMigrationService, LegacySnapshotMigrationService>();
        services.AddSingleton<IActiveModImportService, ActiveModImportService>();
        services.AddSingleton<IExistingModRootAdoptionService, ExistingModRootAdoptionService>();
        services.AddSingleton<IExistingModRootPreviewGuard, ExistingModRootPreviewGuard>();
        services.AddSingleton<ILineDiffService, MyersLineDiffService>();
        services.AddSingleton<IProfileRepository, JsonProfileRepository>();
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<IProfileInterchange, JsonProfileInterchange>();
        services.AddSingleton<ILegacyProfileParser, LegacyProfileParser>();
        services.AddSingleton<ILegacyProfileImportService, LegacyProfileImportService>();
        services.AddSingleton<ILegacyProfileExportService, LegacyProfileExportService>();
        services.AddSingleton<IGameLaunchCoordinator, GameLaunchCoordinator>();
        services.AddSingleton<ILocalLog>(_ => new RollingLocalFileLog(RollingLocalFileLogOptions.Create(Path.Combine(paths.StateDirectory, "Logs"))));
        services.AddSingleton<IApplicationVersionProvider>(_ => new AssemblyApplicationVersionProvider(typeof(App).Assembly));
        services.AddSingleton<IApplicationDiagnostics, ApplicationDiagnostics>();
        services.AddSingleton<IWorkshopPreviewCache, WorkshopPreviewCache>();
        services.AddSingleton(_ => new HttpClient());
        services.AddSingleton<IReleaseService, GitHubReleaseService>();
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();
        services.AddSingleton(_ => SteamWorkshopClient.Create());
        services.AddSingleton<IWorkshopService>(serviceProvider => serviceProvider.GetRequiredService<SteamWorkshopClient>().Workshop);
    }
}
