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
    private CancellationTokenSource? previewOperation;
    private long previewRevision;
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
    public bool ReplaceActiveSet
    {
        get => replaceActiveSet;
        set
        {
            if (replaceActiveSet == value) return;
            this.RaiseAndSetIfChanged(ref replaceActiveSet, value);
            InvalidateAllPreviews();
            Report = "Active-mod import mode changed. Preview again before confirmation.";
        }
    }
    public bool CanPreviewActiveMods => session.Settings is { } settings && gameSource.SupportsActiveMods(settings.SelectedGame);
    public bool CanPreviewModRoots => session.Settings is { } settings && gameSource.SupportsModRoots(settings.SelectedGame);
    public bool CanPreviewOverrideCleanup => session.Settings is { } settings && gameSource.SupportsOverrideCleanup(settings.SelectedGame);
    public string CapabilityGuidance => gameSource.Capabilities.Guidance;
    public bool CanApplyActiveMods => activePreview is not null;
    public bool CanApplySnapshots => snapshotPreview is not null;
    public bool CanApplyOverrideCleanup => overridePreview is not null;
    public bool CanApplyModRoots => rootPreview is not null;
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
        var operation = BeginPreview();
        if (session.Settings is not { } settings) return Result.Failure("AAML is not initialized.");
        var discovered = session.DiscoveredMods.ToArray();
        var loaded = await gameSource.ReadActiveModsAsync(settings.SelectedGame, settings.LocationFor(settings.SelectedGame).InstallationLocation, operation.Token);
        if (!IsCurrent(operation.Revision)) return Result.Failure("Migration inputs changed while previewing. Preview again.");
        if (!loaded.IsSuccess) return Result.Failure(loaded.Error!.Message);
        var preview = activeImport.Preview(settings.SelectedGame, ReplaceActiveSet ? ActiveModImportMode.Replace : ActiveModImportMode.Merge, loaded.Value!, discovered, settings);
        if (!preview.IsSuccess) return Result.Failure(preview.Error!.Message);
        activePreview = preview.Value; this.RaisePropertyChanged(nameof(CanApplyActiveMods)); Report = activePreview!.Report; return Result.Success();
    }

    private async Task<Result> ApplyActiveAsync()
    {
        if (activePreview is null || session.Settings is null) return Result.Failure("Preview active mods before applying.");
        var revision = previewRevision;
        var cancellationToken = previewOperation?.Token ?? CancellationToken.None;
        AAML.Application.Common.Result<AAML.Application.Settings.ApplicationSettings> applied;
        try { applied = await activeImport.ApplyAsync(activePreview, session.DiscoveredMods, session.Settings, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Result.Failure("Migration inputs changed while applying. Preview again."); }
        if (!IsCurrent(revision)) return Result.Failure("Migration inputs changed while applying. Preview again.");
        if (!applied.IsSuccess) return Result.Failure(applied.Error!.Message);
        InvalidateAllPreviews(); return ToCommand(await session.AcceptMigratedSettingsAsync(applied.Value!, CancellationToken.None));
    }

    private async Task<Result> PreviewSnapshotsAsync()
    {
        var operation = BeginPreview();
        var opened = await transfer.OpenLegacySettingsAsync(operation.Token);
        if (!IsCurrent(operation.Revision)) return Result.Failure("Migration inputs changed while previewing. Preview again.");
        if (!opened.IsSuccess) return Result.Failure(opened.Error!.Message); if (opened.Value is null) return Result.Success();
        var preview = await snapshots.PreviewAsync(opened.Value.Value.Path, opened.Value.Value.Contents, operation.Token);
        if (!IsCurrent(operation.Revision)) return Result.Failure("Migration inputs changed while previewing. Preview again.");
        if (!preview.IsSuccess) return Result.Failure(preview.Error!.Message);
        snapshotPreview = preview.Value; this.RaisePropertyChanged(nameof(CanApplySnapshots)); Report = snapshotPreview!.Report; return Result.Success();
    }

    private async Task<Result> ApplySnapshotsAsync()
    {
        if (snapshotPreview is null) return Result.Failure("Preview legacy snapshots before applying.");
        var revision = previewRevision;
        var cancellationToken = previewOperation?.Token ?? CancellationToken.None;
        AAML.Application.Common.Result result;
        try { result = await snapshots.ApplyAsync(snapshotPreview, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Result.Failure("Migration inputs changed while applying. Preview again."); }
        if (!IsCurrent(revision)) return Result.Failure("Migration inputs changed while applying. Preview again.");
        if (result.IsSuccess) InvalidateAllPreviews(); return ToCommand(result);
    }

    private async Task<Result> PreviewOverrideAsync()
    {
        var operation = BeginPreview();
        if (session.Settings is not { } settings) return Result.Failure("AAML is not initialized.");
        var preview = await gameSource.PreviewOverrideCleanupAsync(settings.SelectedGame, operation.Token);
        if (!IsCurrent(operation.Revision)) return Result.Failure("Migration inputs changed while previewing. Preview again.");
        if (!preview.IsSuccess) return Result.Failure(preview.Error!.Message);
        overridePreview = preview.Value; this.RaisePropertyChanged(nameof(CanApplyOverrideCleanup)); Report = overridePreview!.Report; return Result.Success();
    }

    private async Task<Result> ApplyOverrideAsync()
    {
        if (overridePreview is null) return Result.Failure("Preview override cleanup before applying.");
        var revision = previewRevision;
        var cancellationToken = previewOperation?.Token ?? CancellationToken.None;
        AAML.Application.Common.Result result;
        try { result = await gameSource.ApplyOverrideCleanupAsync(overridePreview, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Result.Failure("Migration inputs changed while applying. Preview again."); }
        if (!IsCurrent(revision)) return Result.Failure("Migration inputs changed while applying. Preview again.");
        if (result.IsSuccess) InvalidateAllPreviews(); return ToCommand(result);
    }

    private async Task<Result> PreviewModRootsAsync()
    {
        var operation = BeginPreview();
        if (session.Settings is not { } settings) return Result.Failure("AAML is not initialized.");
        var preview = await gameSource.ReadModRootsAsync(settings.SelectedGame, settings.GameInstallationLocation, settings.ModRootLocations, operation.Token);
        if (!IsCurrent(operation.Revision)) return Result.Failure("Migration inputs changed while previewing. Preview again.");
        if (!preview.IsSuccess) return Result.Failure(preview.Error!.Message);
        rootPreview = preview.Value;
        this.RaisePropertyChanged(nameof(CanApplyModRoots));
        foreach (var row in rootPreview!.Rows) ModRootRows.Add(new ModRootMigrationRowViewModel(row));
        rootGuard.Register(rootPreview);
        Report = rootPreview.Report;
        return Result.Success();
    }

    private ExistingModRootPreview? rootPreview;

    private async Task<Result> ApplyModRootsAsync()
    {
        if (rootPreview is null || session.Settings is null) return Result.Failure("Preview existing mod roots before confirming.");
        var revision = previewRevision;
        var cancellationToken = previewOperation?.Token ?? CancellationToken.None;
        var selected = ModRootRows.Where(row => row.IsSelected).Select(row => row.Index).ToHashSet();
        AAML.Application.Common.Result<AAML.Application.Settings.ApplicationSettings> applied;
        try { applied = await rootAdoption.ApplyAsync(rootPreview, selected, session.Settings, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Result.Failure("Migration inputs changed while applying. Preview again."); }
        if (!IsCurrent(revision)) return Result.Failure("Migration inputs changed while applying. Preview again.");
        if (!applied.IsSuccess) return Result.Failure(applied.Error!.Message);
        InvalidateAllPreviews();
        Report = $"ModRootDirs adoption confirmed. Adopted {selected.Count:N0} selected valid root{(selected.Count == 1 ? string.Empty : "s")}; source INI unchanged.";
        return ToCommand(await session.AcceptMigratedSettingsAsync(applied.Value!, CancellationToken.None));
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ApplicationSession.Settings))
        {
            InvalidateAllPreviews();
            this.RaisePropertyChanged(nameof(CanPreviewActiveMods));
            this.RaisePropertyChanged(nameof(CanPreviewModRoots));
            this.RaisePropertyChanged(nameof(CanPreviewOverrideCleanup));
            Report = "Game-file migration previews were cleared because the selected game or settings changed. Preview again before confirmation.";
        }
    }

    private void ClearModRootPreview()
    {
        rootPreview = null;
        this.RaisePropertyChanged(nameof(CanApplyModRoots));
        ModRootRows.Clear();
        rootGuard.Clear();
    }

    private void ClearActivePreview() { activePreview = null; this.RaisePropertyChanged(nameof(CanApplyActiveMods)); }
    private void ClearSnapshotPreview() { snapshotPreview = null; this.RaisePropertyChanged(nameof(CanApplySnapshots)); }
    private void ClearOverridePreview() { overridePreview = null; this.RaisePropertyChanged(nameof(CanApplyOverrideCleanup)); }
    private (long Revision, CancellationToken Token) BeginPreview()
    {
        InvalidateAllPreviews();
        previewOperation = new CancellationTokenSource();
        return (previewRevision, previewOperation.Token);
    }
    private bool IsCurrent(long revision) => revision == previewRevision && previewOperation?.IsCancellationRequested == false;
    private void InvalidateAllPreviews()
    {
        previewRevision++;
        previewOperation?.Cancel();
        previewOperation?.Dispose();
        previewOperation = null;
        ClearActivePreview();
        ClearSnapshotPreview();
        ClearOverridePreview();
        ClearModRootPreview();
    }

    private static Result ToCommand(AAML.Application.Common.Result result) => result.IsSuccess ? Result.Success() : Result.Failure(result.Error!.Message);
    public void Dispose() { session.PropertyChanged -= OnSessionPropertyChanged; InvalidateAllPreviews(); }
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
