using AAML.Domain.Games;
using CSharpFunctionalExtensions;
using ReactiveUI;
using Zafiro.UI.Commands;
using Zafiro.UI.Shell.Utils;
using AAML.Application.Settings;
using AAML.Domain.Launching;

namespace AAML.Avalonia;

[Section("dashboard", "fa-home", 0, FriendlyName = "Dashboard")]
public sealed class DashboardViewModel : ReactiveObject, IDisposable
{
    private const string AutoSaveOwner = "dashboard";
    private readonly ApplicationSession session;
    private GameVariant selectedGame = GameVariant.XCom2;
    private string gameInstallationPath = string.Empty;
    private bool allowLaunchWithMissingDependencies;
    private string launchArguments = string.Empty;
    private string modRoots = string.Empty;
    private bool closeAfterLaunch;
    private WorkshopStartupRefreshPolicy workshopStartupRefresh = WorkshopStartupRefreshPolicy.AllMods;
    private ThemePreference theme;
    private bool allowMultipleInstances;
    private bool checkForUpdates = true;
    private UpdateChannelPreference updateChannel;
    private bool autoSaveChanges;
    private bool preferencesLoaded;
    private bool preferencesDirty;
    private long preferencesRevision;
    private bool disposed;
    private readonly IApplicationUiController ui;

    public DashboardViewModel(ApplicationSession session, IApplicationUiController ui)
    {
        this.session = session;
        this.ui = ui;
        session.PropertyChanged += OnSessionPropertyChanged;
        session.RegisterAutoSaveOwner(AutoSaveOwner, () => preferencesDirty, SavePreferencesCoreAsync);
        LoadPreferencesDraft();
        SaveGame = ReactiveCommand.CreateFromTask(async () => (await session.SelectGameAsync(SelectedGame, CancellationToken.None)).IsSuccess ? Result.Success() : Result.Failure(session.Status)).Enhance(text: "Save game", name: "SaveGame");
        SaveInstallation = ReactiveCommand.CreateFromTask(async () => (await session.SetGameInstallationAsync(GameInstallationPath, CancellationToken.None)).IsSuccess ? Result.Success() : Result.Failure(session.Status)).Enhance(text: "Save installation", name: "SaveInstallation");
        DetectSteam = ReactiveCommand.CreateFromTask(async () => (await session.DetectSteamAsync(CancellationToken.None)).IsSuccess ? Result.Success() : Result.Failure(session.Status)).Enhance(text: "Detect Steam", name: "DetectSteam");
        SavePreferences = ReactiveCommand.CreateFromTask(SavePreferencesAsync).Enhance(text: "Save preferences", name: "SavePreferences");
        DiscardPreferences = ReactiveCommand.CreateFromTask(DiscardPreferencesAsync).Enhance(text: "Discard edits", name: "DiscardPreferences");
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
    public bool AllowLaunchWithMissingDependencies { get => allowLaunchWithMissingDependencies; set { if (allowLaunchWithMissingDependencies == value) return; this.RaiseAndSetIfChanged(ref allowLaunchWithMissingDependencies, value); MarkPreferencesDirty(); } }
    public string LaunchArguments { get => launchArguments; set { if (launchArguments == value) return; this.RaiseAndSetIfChanged(ref launchArguments, value); MarkPreferencesDirty(); } }
    public string ModRoots { get => modRoots; set { if (modRoots == value) return; this.RaiseAndSetIfChanged(ref modRoots, value); MarkPreferencesDirty(); } }
    public bool CloseAfterLaunch { get => closeAfterLaunch; set { if (closeAfterLaunch == value) return; this.RaiseAndSetIfChanged(ref closeAfterLaunch, value); MarkPreferencesDirty(); } }
    public WorkshopStartupRefreshPolicy WorkshopStartupRefresh { get => workshopStartupRefresh; set { if (workshopStartupRefresh == value) return; this.RaiseAndSetIfChanged(ref workshopStartupRefresh, value); MarkPreferencesDirty(); } }
    public IReadOnlyList<WorkshopStartupRefreshPolicy> WorkshopStartupRefreshOptions { get; } = [WorkshopStartupRefreshPolicy.AllMods, WorkshopStartupRefreshPolicy.ActiveMods, WorkshopStartupRefreshPolicy.Manual];
    public ThemePreference Theme { get => theme; set { if (theme == value) return; this.RaiseAndSetIfChanged(ref theme, value); ui.ApplyTheme(value); MarkPreferencesDirty(); } }
    public IReadOnlyList<ThemePreference> ThemeOptions { get; } = Enum.GetValues<ThemePreference>();
    public bool AllowMultipleInstances { get => allowMultipleInstances; set { if (allowMultipleInstances == value) return; this.RaiseAndSetIfChanged(ref allowMultipleInstances, value); MarkPreferencesDirty(); } }
    public bool CheckForUpdates { get => checkForUpdates; set { if (checkForUpdates == value) return; this.RaiseAndSetIfChanged(ref checkForUpdates, value); MarkPreferencesDirty(); } }
    public UpdateChannelPreference UpdateChannel { get => updateChannel; set { if (updateChannel == value) return; this.RaiseAndSetIfChanged(ref updateChannel, value); MarkPreferencesDirty(); } }
    public IReadOnlyList<UpdateChannelPreference> UpdateChannelOptions { get; } = Enum.GetValues<UpdateChannelPreference>();
    public bool AutoSaveChanges
    {
        get => autoSaveChanges;
        set
        {
            if (autoSaveChanges == value) return;
            this.RaiseAndSetIfChanged(ref autoSaveChanges, value);
            _ = PersistAutoSavePreferenceAsync(value);
        }
    }

    public void Activate() => session.ActivateAutoSaveOwner(AutoSaveOwner);

    private async Task<Result> SavePreferencesAsync()
    {
        var result = await session.FlushAutoSaveOwnerAsync(AutoSaveOwner, CancellationToken.None);
        return result.IsSuccess ? Result.Success() : Result.Failure(session.Status);
    }

    private async Task<AAML.Application.Common.Result> SavePreferencesCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var revision = preferencesRevision;
            var arguments = LaunchArguments.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => new LaunchArgument(value)).ToArray();
            var roots = ModRoots.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var allowMissing = AllowLaunchWithMissingDependencies;
            var closeAfter = CloseAfterLaunch;
            var startupRefresh = WorkshopStartupRefresh;
            var selectedTheme = Theme;
            var multipleInstances = AllowMultipleInstances;
            var result = await session.SavePreferencesAsync(arguments, roots, allowMissing, closeAfter, startupRefresh, selectedTheme, multipleInstances, CheckForUpdates, UpdateChannel, cancellationToken);
            if (result.IsSuccess && revision == preferencesRevision) preferencesDirty = false;
            return result;
        }
        catch (ArgumentException exception) { return AAML.Application.Common.Result.Failure(new AAML.Application.Common.Error("settings.preference_invalid", exception.Message, AAML.Application.Common.ErrorKind.Validation)); }
    }

    private void MarkPreferencesDirty()
    {
        if (!preferencesLoaded) return;
        preferencesDirty = true;
        preferencesRevision++;
        session.NotifyAutoSaveOwnerChanged(AutoSaveOwner);
    }

    private void LoadPreferencesDraft()
    {
        if (session.Settings is not { } settings) return;
        preferencesLoaded = false;
        selectedGame = settings.SelectedGame;
        gameInstallationPath = settings.GameInstallationLocation ?? string.Empty;
        allowLaunchWithMissingDependencies = settings.AllowLaunchWithMissingDependencies;
        launchArguments = string.Join(Environment.NewLine, settings.LaunchArguments.Select(argument => argument.Value));
        modRoots = string.Join(Environment.NewLine, settings.ModRootLocations);
        closeAfterLaunch = settings.CloseAfterLaunch;
        workshopStartupRefresh = settings.WorkshopStartupRefresh;
        theme = settings.Theme;
        allowMultipleInstances = settings.AllowMultipleInstances;
        checkForUpdates = settings.CheckForUpdates;
        updateChannel = settings.UpdateChannel;
        autoSaveChanges = settings.AutoSaveChanges;
        preferencesDirty = false;
        preferencesLoaded = true;
        foreach (var property in new[] { nameof(SelectedGame), nameof(GameInstallationPath), nameof(AllowLaunchWithMissingDependencies), nameof(LaunchArguments), nameof(ModRoots), nameof(CloseAfterLaunch), nameof(WorkshopStartupRefresh), nameof(Theme), nameof(AllowMultipleInstances), nameof(CheckForUpdates), nameof(UpdateChannel), nameof(AutoSaveChanges) }) this.RaisePropertyChanged(property);
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        this.RaisePropertyChanged(nameof(Status));
        this.RaisePropertyChanged(nameof(Origin));
        if (args.PropertyName == nameof(ApplicationSession.Settings) && !preferencesLoaded) LoadPreferencesDraft();
        if (args.PropertyName == nameof(ApplicationSession.Settings) && session.Settings is { } settings && autoSaveChanges != settings.AutoSaveChanges)
        {
            autoSaveChanges = settings.AutoSaveChanges;
            this.RaisePropertyChanged(nameof(AutoSaveChanges));
        }
    }

    private async Task<Result> DiscardPreferencesAsync()
    {
        session.CancelAutoSaveOwner(AutoSaveOwner);
        var loaded = await session.ReloadSettingsAsync(CancellationToken.None);
        if (!loaded.IsSuccess) return Result.Failure(session.Status);
        preferencesLoaded = false;
        LoadPreferencesDraft();
        ui.ApplyTheme(theme);
        return Result.Success();
    }

    private async Task PersistAutoSavePreferenceAsync(bool enabled)
    {
        var result = await session.SetAutoSaveChangesAsync(enabled, CancellationToken.None);
        if (result.IsSuccess) return;
        autoSaveChanges = session.Settings?.AutoSaveChanges ?? false;
        this.RaisePropertyChanged(nameof(AutoSaveChanges));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        session.PropertyChanged -= OnSessionPropertyChanged;
        session.CancelAutoSaveOwner(AutoSaveOwner);
    }
}
