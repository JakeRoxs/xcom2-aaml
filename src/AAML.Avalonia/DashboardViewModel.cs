using AAML.Domain.Games;
using CSharpFunctionalExtensions;
using ReactiveUI;
using Zafiro.UI.Commands;
using Zafiro.UI.Shell.Utils;
using AAML.Application.Settings;
using AAML.Domain.Launching;

namespace AAML.Avalonia;

[Section("dashboard", "fa-home", 0, FriendlyName = "Dashboard")]
public sealed class DashboardViewModel : ReactiveObject
{
    private readonly ApplicationSession session;
    private GameVariant selectedGame = GameVariant.XCom2;
    private string gameInstallationPath = string.Empty;
    private bool allowLaunchWithMissingDependencies;
    private string launchArguments = string.Empty;
    private string modRoots = string.Empty;
    private bool closeAfterLaunch;
    private WorkshopStartupRefreshPolicy workshopStartupRefresh;
    private ThemePreference theme;
    private bool allowMultipleInstances;
    private bool checkForUpdates = true;
    private UpdateChannelPreference updateChannel;
    private readonly IApplicationUiController ui;

    public DashboardViewModel(ApplicationSession session, IApplicationUiController ui)
    {
        this.session = session;
        this.ui = ui;
        session.PropertyChanged += (_, _) =>
        {
            if (session.Settings is not null) selectedGame = session.Settings.SelectedGame;
            if (session.Settings is not null) gameInstallationPath = session.Settings.GameInstallationLocation ?? string.Empty;
            if (session.Settings is not null) allowLaunchWithMissingDependencies = session.Settings.AllowLaunchWithMissingDependencies;
            if (session.Settings is not null) launchArguments = string.Join(Environment.NewLine, session.Settings.LaunchArguments.Select(argument => argument.Value));
            if (session.Settings is not null) modRoots = string.Join(Environment.NewLine, session.Settings.ModRootLocations);
            if (session.Settings is not null) closeAfterLaunch = session.Settings.CloseAfterLaunch;
            if (session.Settings is not null) workshopStartupRefresh = session.Settings.WorkshopStartupRefresh == WorkshopStartupRefreshPolicy.Manual ? WorkshopStartupRefreshPolicy.AllMods : session.Settings.WorkshopStartupRefresh;
            if (session.Settings is not null) theme = session.Settings.Theme;
            if (session.Settings is not null) allowMultipleInstances = session.Settings.AllowMultipleInstances;
            if (session.Settings is not null) checkForUpdates = session.Settings.CheckForUpdates;
            if (session.Settings is not null) updateChannel = session.Settings.UpdateChannel;
            this.RaisePropertyChanged(nameof(Status));
            this.RaisePropertyChanged(nameof(Origin));
            this.RaisePropertyChanged(nameof(SelectedGame));
            this.RaisePropertyChanged(nameof(GameInstallationPath));
            this.RaisePropertyChanged(nameof(AllowLaunchWithMissingDependencies));
            this.RaisePropertyChanged(nameof(LaunchArguments)); this.RaisePropertyChanged(nameof(ModRoots)); this.RaisePropertyChanged(nameof(CloseAfterLaunch)); this.RaisePropertyChanged(nameof(WorkshopStartupRefresh)); this.RaisePropertyChanged(nameof(Theme)); this.RaisePropertyChanged(nameof(AllowMultipleInstances)); this.RaisePropertyChanged(nameof(CheckForUpdates)); this.RaisePropertyChanged(nameof(UpdateChannel));
        };
        SaveGame = ReactiveCommand.CreateFromTask(async () => (await session.SelectGameAsync(SelectedGame, CancellationToken.None)).IsSuccess ? Result.Success() : Result.Failure(session.Status)).Enhance(text: "Save game", name: "SaveGame");
        SaveInstallation = ReactiveCommand.CreateFromTask(async () => (await session.SetGameInstallationAsync(GameInstallationPath, CancellationToken.None)).IsSuccess ? Result.Success() : Result.Failure(session.Status)).Enhance(text: "Save installation", name: "SaveInstallation");
        DetectSteam = ReactiveCommand.CreateFromTask(async () => (await session.DetectSteamAsync(CancellationToken.None)).IsSuccess ? Result.Success() : Result.Failure(session.Status)).Enhance(text: "Detect Steam", name: "DetectSteam");
        SavePreferences = ReactiveCommand.CreateFromTask(SavePreferencesAsync).Enhance(text: "Save preferences", name: "SavePreferences");
        DiscardPreferences = ReactiveCommand.CreateFromTask(async () => (await session.ReloadSettingsAsync(CancellationToken.None)).IsSuccess ? Result.Success() : Result.Failure(session.Status)).Enhance(text: "Discard edits", name: "DiscardPreferences");
        ApplyConfiguration = ReactiveCommand.CreateFromTask(async () => (await session.ApplyConfigurationAsync(CancellationToken.None)).IsSuccess ? Result.Success() : Result.Failure(session.Status)).Enhance(text: "Apply configuration", name: "ApplyConfiguration");
        Launch = ReactiveCommand.CreateFromTask(async () => { var result = await session.LaunchAsync(CancellationToken.None); if (result.IsSuccess && session.Settings?.CloseAfterLaunch == true) ui.Shutdown(); return result.IsSuccess ? Result.Success() : Result.Failure(session.Status); }).Enhance(text: "Launch game", name: "LaunchGame");
    }

    public IReadOnlyList<GameVariant> Games { get; } = Enum.GetValues<GameVariant>();
    public IEnhancedCommand<Result> SaveGame { get; }
    public IEnhancedCommand<Result> SaveInstallation { get; }
    public IEnhancedCommand<Result> DetectSteam { get; }
    public IEnhancedCommand<Result> Launch { get; }
    public IEnhancedCommand<Result> SavePreferences { get; }
    public IEnhancedCommand<Result> DiscardPreferences { get; }
    public IEnhancedCommand<Result> ApplyConfiguration { get; }
    public string Status => session.Status;
    public string Origin => session.Origin?.ToString() ?? "Not loaded";
    public string GameInstallationPath { get => gameInstallationPath; set => this.RaiseAndSetIfChanged(ref gameInstallationPath, value); }
    public GameVariant SelectedGame { get => selectedGame; set => this.RaiseAndSetIfChanged(ref selectedGame, value); }
    public bool AllowLaunchWithMissingDependencies { get => allowLaunchWithMissingDependencies; set => this.RaiseAndSetIfChanged(ref allowLaunchWithMissingDependencies, value); }
    public string LaunchArguments { get => launchArguments; set => this.RaiseAndSetIfChanged(ref launchArguments, value); }
    public string ModRoots { get => modRoots; set => this.RaiseAndSetIfChanged(ref modRoots, value); }
    public bool CloseAfterLaunch { get => closeAfterLaunch; set => this.RaiseAndSetIfChanged(ref closeAfterLaunch, value); }
    public WorkshopStartupRefreshPolicy WorkshopStartupRefresh { get => workshopStartupRefresh; set => this.RaiseAndSetIfChanged(ref workshopStartupRefresh, value); }
    public IReadOnlyList<WorkshopStartupRefreshPolicy> WorkshopStartupRefreshOptions { get; } = [WorkshopStartupRefreshPolicy.AllMods, WorkshopStartupRefreshPolicy.ActiveMods];
    public ThemePreference Theme { get => theme; set => this.RaiseAndSetIfChanged(ref theme, value); }
    public IReadOnlyList<ThemePreference> ThemeOptions { get; } = Enum.GetValues<ThemePreference>();
    public bool AllowMultipleInstances { get => allowMultipleInstances; set => this.RaiseAndSetIfChanged(ref allowMultipleInstances, value); }
    public bool CheckForUpdates { get => checkForUpdates; set => this.RaiseAndSetIfChanged(ref checkForUpdates, value); }
    public UpdateChannelPreference UpdateChannel { get => updateChannel; set => this.RaiseAndSetIfChanged(ref updateChannel, value); }
    public IReadOnlyList<UpdateChannelPreference> UpdateChannelOptions { get; } = Enum.GetValues<UpdateChannelPreference>();

    private async Task<Result> SavePreferencesAsync()
    {
        try
        {
            var arguments = LaunchArguments.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => new LaunchArgument(value)).ToArray();
            var roots = ModRoots.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var allowMissing = AllowLaunchWithMissingDependencies;
            var closeAfter = CloseAfterLaunch;
            var startupRefresh = WorkshopStartupRefresh;
            var selectedTheme = Theme;
            var multipleInstances = AllowMultipleInstances;
            var result = await session.SavePreferencesAsync(arguments, roots, allowMissing, closeAfter, startupRefresh, selectedTheme, multipleInstances, CheckForUpdates, UpdateChannel, CancellationToken.None);
            if (result.IsSuccess) ui.ApplyTheme(selectedTheme);
            return result.IsSuccess ? Result.Success() : Result.Failure(session.Status);
        }
        catch (ArgumentException exception) { return Result.Failure(exception.Message); }
    }
}
