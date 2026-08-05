using AAML.Application.Configurations;
using CSharpFunctionalExtensions;
using ReactiveUI;
using System.Collections.ObjectModel;
using Zafiro.UI.Commands;
using Zafiro.UI.Shell.Utils;

namespace AAML.Avalonia;

[Section("migration", "fa-arrow-right-arrow-left", 5, FriendlyName = "Migration")]
public sealed class MigrationViewModel : ReactiveObject, IDisposable
{
    private readonly ApplicationSession session;
    private readonly ILegacyGameConfigurationSource gameSource;
    private readonly IActiveModImportService activeImport;
    private readonly ILegacySnapshotMigrationService snapshots;
    private readonly IProfileDocumentTransfer transfer;
    private readonly IExistingModRootAdoptionService rootAdoption;
    private readonly IExistingModRootPreviewGuard rootGuard;
    private ActiveModImportPreview? activePreview;
    private ObsoleteOverridePreview? overridePreview;
    private LegacySnapshotMigrationPreview? snapshotPreview;
    private string report = "Preview a migration to inspect every proposed action. Source files are never modified.";
    private bool replaceActiveSet = true;

    public MigrationViewModel(ApplicationSession session, ILegacyGameConfigurationSource gameSource, IActiveModImportService activeImport, ILegacySnapshotMigrationService snapshots, IProfileDocumentTransfer transfer, IExistingModRootAdoptionService rootAdoption, IExistingModRootPreviewGuard rootGuard)
    {
        this.session = session; this.gameSource = gameSource; this.activeImport = activeImport; this.snapshots = snapshots; this.transfer = transfer; this.rootAdoption = rootAdoption; this.rootGuard = rootGuard;
        session.PropertyChanged += OnSessionPropertyChanged;
        PreviewActiveMods = ReactiveCommand.CreateFromTask(PreviewActiveAsync).Enhance(text: "Preview active mods", name: "PreviewActiveModsMigration");
        ApplyActiveMods = ReactiveCommand.CreateFromTask(ApplyActiveAsync).Enhance(text: "Apply active mods", name: "ApplyActiveModsMigration");
        PreviewSnapshots = ReactiveCommand.CreateFromTask(PreviewSnapshotsAsync).Enhance(text: "Preview snapshots", name: "PreviewLegacySnapshots");
        ApplySnapshots = ReactiveCommand.CreateFromTask(ApplySnapshotsAsync).Enhance(text: "Apply snapshots", name: "ApplyLegacySnapshots");
        PreviewOverrideCleanup = ReactiveCommand.CreateFromTask(PreviewOverrideAsync).Enhance(text: "Preview override cleanup", name: "PreviewOverrideCleanup");
        ApplyOverrideCleanup = ReactiveCommand.CreateFromTask(ApplyOverrideAsync).Enhance(text: "Apply override cleanup", name: "ApplyOverrideCleanup");
        PreviewModRoots = ReactiveCommand.CreateFromTask(PreviewModRootsAsync).Enhance(text: "Preview mod roots", name: "PreviewModRootsMigration");
        ApplyModRoots = ReactiveCommand.CreateFromTask(ApplyModRootsAsync).Enhance(text: "Confirm selected roots", name: "ApplyModRootsMigration");
    }

    public string Report { get => report; private set => this.RaiseAndSetIfChanged(ref report, value); }
    public bool ReplaceActiveSet { get => replaceActiveSet; set => this.RaiseAndSetIfChanged(ref replaceActiveSet, value); }
    public IEnhancedCommand<Result> PreviewActiveMods { get; }
    public IEnhancedCommand<Result> ApplyActiveMods { get; }
    public IEnhancedCommand<Result> PreviewSnapshots { get; }
    public IEnhancedCommand<Result> ApplySnapshots { get; }
    public IEnhancedCommand<Result> PreviewOverrideCleanup { get; }
    public IEnhancedCommand<Result> ApplyOverrideCleanup { get; }
    public IEnhancedCommand<Result> PreviewModRoots { get; }
    public IEnhancedCommand<Result> ApplyModRoots { get; }
    public ObservableCollection<ModRootMigrationRowViewModel> ModRootRows { get; } = [];

    private async Task<Result> PreviewActiveAsync()
    {
        if (session.Settings is null) return Result.Failure("AAML is not initialized.");
        var loaded = await gameSource.ReadActiveModsAsync(session.Settings.SelectedGame, session.Settings.LocationFor(session.Settings.SelectedGame).InstallationLocation, CancellationToken.None);
        if (!loaded.IsSuccess) return Result.Failure(loaded.Error!.Message);
        var preview = activeImport.Preview(session.Settings.SelectedGame, ReplaceActiveSet ? ActiveModImportMode.Replace : ActiveModImportMode.Merge, loaded.Value!, session.DiscoveredMods, session.Settings);
        if (!preview.IsSuccess) return Result.Failure(preview.Error!.Message);
        activePreview = preview.Value; Report = activePreview!.Report; return Result.Success();
    }

    private async Task<Result> ApplyActiveAsync()
    {
        if (activePreview is null || session.Settings is null) return Result.Failure("Preview active mods before applying.");
        var applied = await activeImport.ApplyAsync(activePreview, session.DiscoveredMods, session.Settings, CancellationToken.None);
        if (!applied.IsSuccess) return Result.Failure(applied.Error!.Message);
        activePreview = null; return ToCommand(await session.AcceptMigratedSettingsAsync(applied.Value!, CancellationToken.None));
    }

    private async Task<Result> PreviewSnapshotsAsync()
    {
        var opened = await transfer.OpenLegacySettingsAsync(CancellationToken.None);
        if (!opened.IsSuccess) return Result.Failure(opened.Error!.Message); if (opened.Value is null) return Result.Success();
        var preview = await snapshots.PreviewAsync(opened.Value.Value.Path, opened.Value.Value.Contents, CancellationToken.None);
        if (!preview.IsSuccess) return Result.Failure(preview.Error!.Message);
        snapshotPreview = preview.Value; Report = snapshotPreview!.Report; return Result.Success();
    }

    private async Task<Result> ApplySnapshotsAsync()
    {
        if (snapshotPreview is null) return Result.Failure("Preview legacy snapshots before applying.");
        var result = await snapshots.ApplyAsync(snapshotPreview, CancellationToken.None); if (result.IsSuccess) snapshotPreview = null; return ToCommand(result);
    }

    private async Task<Result> PreviewOverrideAsync()
    {
        if (session.Settings is null) return Result.Failure("AAML is not initialized.");
        var preview = await gameSource.PreviewOverrideCleanupAsync(session.Settings.SelectedGame, CancellationToken.None);
        if (!preview.IsSuccess) return Result.Failure(preview.Error!.Message);
        overridePreview = preview.Value; Report = overridePreview!.Report; return Result.Success();
    }

    private async Task<Result> ApplyOverrideAsync()
    {
        if (overridePreview is null) return Result.Failure("Preview override cleanup before applying.");
        var result = await gameSource.ApplyOverrideCleanupAsync(overridePreview, CancellationToken.None); if (result.IsSuccess) overridePreview = null; return ToCommand(result);
    }

    private async Task<Result> PreviewModRootsAsync()
    {
        ClearModRootPreview();
        if (session.Settings is not { } settings) return Result.Failure("AAML is not initialized.");
        var preview = await gameSource.ReadModRootsAsync(settings.SelectedGame, settings.GameInstallationLocation, settings.ModRootLocations, CancellationToken.None);
        if (!preview.IsSuccess) return Result.Failure(preview.Error!.Message);
        rootPreview = preview.Value;
        foreach (var row in rootPreview!.Rows) ModRootRows.Add(new ModRootMigrationRowViewModel(row));
        rootGuard.Register(rootPreview);
        Report = rootPreview.Report;
        return Result.Success();
    }

    private ExistingModRootPreview? rootPreview;

    private async Task<Result> ApplyModRootsAsync()
    {
        if (rootPreview is null || session.Settings is null) return Result.Failure("Preview existing mod roots before confirming.");
        var selected = ModRootRows.Where(row => row.IsSelected).Select(row => row.Index).ToHashSet();
        var applied = await rootAdoption.ApplyAsync(rootPreview, selected, session.Settings, CancellationToken.None);
        if (!applied.IsSuccess) return Result.Failure(applied.Error!.Message);
        rootGuard.Clear();
        rootPreview = null;
        ModRootRows.Clear();
        Report = $"ModRootDirs adoption confirmed. Adopted {selected.Count:N0} selected valid root{(selected.Count == 1 ? string.Empty : "s")}; source INI unchanged.";
        return ToCommand(await session.AcceptMigratedSettingsAsync(applied.Value!, CancellationToken.None));
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ApplicationSession.Settings) && rootPreview is not null)
        {
            ClearModRootPreview();
            Report = "Existing ModRootDirs preview cleared because the selected game or settings changed. Preview again before adoption.";
        }
    }

    private void ClearModRootPreview()
    {
        rootPreview = null;
        ModRootRows.Clear();
        rootGuard.Clear();
    }

    private static Result ToCommand(AAML.Application.Common.Result result) => result.IsSuccess ? Result.Success() : Result.Failure(result.Error!.Message);
    public void Dispose() { session.PropertyChanged -= OnSessionPropertyChanged; ClearModRootPreview(); }
}

public sealed class ModRootMigrationRowViewModel(ExistingModRootRow row) : ReactiveObject
{
    private bool isSelected;
    public int Index => row.Index;
    public string Source => row.RawValue;
    public string ResolvedPath => row.ResolvedPath ?? string.Empty;
    public string Resolution => row.Resolution.ToString();
    public bool CanSelect => row.Resolution == ExistingModRootResolution.Valid;
    public bool IsSelected { get => isSelected; set { if (!CanSelect) return; this.RaiseAndSetIfChanged(ref isSelected, value); } }
}
