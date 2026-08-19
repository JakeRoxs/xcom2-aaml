using System.Runtime.InteropServices;
using AAML.Application.Diagnostics;
using AAML.Application.Ports;
using CSharpFunctionalExtensions;
using ReactiveUI;
using Zafiro.UI.Commands;
using Zafiro.UI.Shell.Utils;
using AAML.Application;

namespace AAML.Avalonia;

[Section("support", "fa-circle-question", 6, FriendlyName = "Support")]
public sealed class SupportViewModel : ReactiveObject, IDisposable
{
    private readonly ApplicationSession session;
    private readonly IExternalLauncher launcher;
    private readonly IApplicationPaths paths;
    private readonly IApplicationDiagnostics diagnostics;
    private readonly IApplicationUiController ui;
    private readonly IGameUserDataLocator gameUserData;

    public SupportViewModel(ApplicationSession session, IExternalLauncher launcher, IApplicationPaths paths, IApplicationDiagnostics diagnostics, IApplicationVersionProvider versions, IApplicationUiController ui, IGameLogLocator gameLogs, IGameUserDataLocator gameUserData)
    {
        this.session = session; this.launcher = launcher; this.paths = paths; this.diagnostics = diagnostics; this.ui = ui; this.gameUserData = gameUserData;
        session.PropertyChanged += OnSessionPropertyChanged;
        var version = versions.GetCurrentVersion(); Version = version.IsSuccess ? version.Value! : "unknown";
        CheckUpdates = ReactiveCommand.CreateFromTask(CheckUpdatesAsync).Enhance(text: "Check for updates", name: "CheckUpdates");
        OpenRelease = ReactiveCommand.CreateFromTask(() => OpenUriAsync(session.LatestRelease?.PageUri)).Enhance(text: "View release", name: "OpenRelease");
        CopyReport = ReactiveCommand.CreateFromTask(async () => await ui.CopyTextAsync(diagnostics.BuildReport()) ? Result.Success() : Result.Failure("Clipboard is unavailable.")).Enhance(text: "Copy diagnostic report", name: "CopyDiagnostics");
        OpenLogs = ReactiveCommand.CreateFromTask(() => OpenDirectoryAsync(diagnostics.LogDirectory, true)).Enhance(text: "Open logs folder", name: "OpenLogs");
        OpenLog = ReactiveCommand.CreateFromTask(() => ToCommand(launcher.OpenFileAsync(diagnostics.ActiveLogPath, CancellationToken.None))).Enhance(text: "Open active log", name: "OpenLog");
        OpenProject = Command(ProjectIdentity.RepositoryUri, "OpenProject"); OpenIssues = Command(ProjectIdentity.IssuesUri, "OpenIssues"); OpenWiki = Command(ProjectIdentity.WikiUri, "OpenWiki");
        OpenApplication = ReactiveCommand.CreateFromTask(() => OpenDirectoryAsync(AppContext.BaseDirectory, false)).Enhance(text: "Open application folder", name: "OpenApplicationFolder");
        OpenLicense = ReactiveCommand.CreateFromTask(OpenLicenseAsync).Enhance(text: "View license", name: "OpenLicense");
        OpenConfig = Folder(paths.ConfigurationDirectory, "OpenConfigFolder"); OpenData = Folder(paths.DataDirectory, "OpenDataFolder"); OpenState = Folder(paths.StateDirectory, "OpenStateFolder"); OpenCache = Folder(paths.CacheDirectory, "OpenCacheFolder"); OpenRuntime = Folder(paths.RuntimeDirectory, "OpenRuntimeFolder");
        OpenGame = ReactiveCommand.CreateFromTask(() => string.IsNullOrWhiteSpace(session.Settings?.GameInstallationLocation) ? Task.FromResult(Result.Failure("Game installation is not configured.")) : OpenDirectoryAsync(session.Settings.GameInstallationLocation, false)).Enhance(text: "Open game folder", name: "OpenGameFolder");
        OpenGameUserData = ReactiveCommand.CreateFromTask(() => OpenGameDataAsync(configuration: false)).Enhance(text: "Open game user data", name: "OpenGameUserData");
        OpenGameConfiguration = ReactiveCommand.CreateFromTask(() => OpenGameDataAsync(configuration: true)).Enhance(text: "Open game configuration", name: "OpenGameConfiguration");
        OpenGameLog = ReactiveCommand.CreateFromTask(() => session.Settings is null || gameLogs.GetCurrentLogPath(session.Settings.SelectedGame) is not { } path ? Task.FromResult(Result.Failure("The selected platform does not expose a qualified game-log path.")) : ToCommand(launcher.OpenFileAsync(path, CancellationToken.None))).Enhance(text: "Open game log", name: "OpenGameLog");
    }

    public string Product => "Avalonia Alternative Mod Launcher";
    public string Version { get; }
    public string Runtime => RuntimeInformation.FrameworkDescription;
    public string Platform => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})";
    public string License => "GNU General Public License v3.0";
    public string LogPath => diagnostics.ActiveLogPath;
    public string UpdateDetails => session.UpdateCheck?.Details ?? "No update check has run in this session.";
    public IEnhancedCommand<Result> CheckUpdates { get; } public IEnhancedCommand<Result> OpenRelease { get; } public IEnhancedCommand<Result> CopyReport { get; } public IEnhancedCommand<Result> OpenLogs { get; } public IEnhancedCommand<Result> OpenLog { get; }
    public IEnhancedCommand<Result> OpenProject { get; } public IEnhancedCommand<Result> OpenIssues { get; } public IEnhancedCommand<Result> OpenWiki { get; }
    public IEnhancedCommand<Result> OpenApplication { get; } public IEnhancedCommand<Result> OpenLicense { get; } public IEnhancedCommand<Result> OpenConfig { get; } public IEnhancedCommand<Result> OpenData { get; } public IEnhancedCommand<Result> OpenState { get; } public IEnhancedCommand<Result> OpenCache { get; } public IEnhancedCommand<Result> OpenRuntime { get; } public IEnhancedCommand<Result> OpenGame { get; } public IEnhancedCommand<Result> OpenGameUserData { get; } public IEnhancedCommand<Result> OpenGameConfiguration { get; } public IEnhancedCommand<Result> OpenGameLog { get; }

    private async Task<Result> CheckUpdatesAsync() { var result = await session.CheckForUpdatesAsync(true, CancellationToken.None); return result.IsSuccess ? Result.Success() : Result.Failure(result.Error!.Message); }
    private IEnhancedCommand<Result> Command(Uri uri, string name) => ReactiveCommand.CreateFromTask(() => OpenUriAsync(uri)).Enhance(text: name, name: name);
    private IEnhancedCommand<Result> Folder(string path, string name) => ReactiveCommand.CreateFromTask(() => OpenDirectoryAsync(path, true)).Enhance(text: name, name: name);
    private async Task<Result> OpenDirectoryAsync(string path, bool create) { if (create) Directory.CreateDirectory(path); return await ToCommand(launcher.OpenDirectoryAsync(path, CancellationToken.None)); }
    private async Task<Result> OpenGameDataAsync(bool configuration)
    {
        if (session.Settings is not { } settings) return Result.Failure("AAML is not initialized.");
        var located = gameUserData.Locate(settings.SelectedGame, settings.LocationFor(settings.SelectedGame).InstallationLocation);
        if (!located.IsSuccess) return Result.Failure(located.Error!.Message);
        var path = configuration ? located.Value!.ConfigurationDirectory : located.Value!.UserDataDirectory;
        if (!Directory.Exists(path))
            return Result.Failure(configuration
                ? "The selected game's generated configuration directory does not exist. Launch that game variant once, then try again."
                : "The selected game's user-data directory does not exist. Launch that game variant once, then try again.");
        return await ToCommand(launcher.OpenDirectoryAsync(path, CancellationToken.None));
    }
    private Task<Result> OpenUriAsync(Uri? uri) => uri is null ? Task.FromResult(Result.Failure("No newer release is available.")) : ToCommand(launcher.OpenUriAsync(uri, CancellationToken.None));
    private Task<Result> OpenLicenseAsync() { var path = Path.Combine(AppContext.BaseDirectory, "licenses", "AAML-GPL-3.0.txt"); return File.Exists(path) ? ToCommand(launcher.OpenFileAsync(path, CancellationToken.None)) : OpenUriAsync(ProjectIdentity.LicenseUri); }
    private static async Task<Result> ToCommand(Task<AAML.Application.Common.Result> task) { var result = await task; return result.IsSuccess ? Result.Success() : Result.Failure(result.Error!.Message); }
    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ApplicationSession.UpdateCheck)) this.RaisePropertyChanged(nameof(UpdateDetails));
    }
    public void Dispose() => session.PropertyChanged -= OnSessionPropertyChanged;
}
