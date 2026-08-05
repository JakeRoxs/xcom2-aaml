using System.Collections.ObjectModel;
using AAML.Application.Configurations;
using CSharpFunctionalExtensions;
using ReactiveUI;
using Zafiro.UI.Commands;
using Zafiro.UI.Shell.Utils;

namespace AAML.Avalonia;

[Section("configurations", "fa-file-code", 3, FriendlyName = "Configurations")]
public sealed class ConfigurationsViewModel : ReactiveObject, IDisposable
{
    private static readonly ConfigurationFileLimits FileLimits = new(8_000_000, 4_000_000, 100_000);
    private readonly ApplicationSession session;
    private readonly IConfigurationFileRepository files;
    private readonly IConfigurationSnapshotRepository snapshots;
    private readonly ILineDiffService diff;
    private ConfigurationDocumentSummary? selectedDocument;
    private ConfigurationEditorState? editor;
    private SavedConfigurationSnapshot? snapshot;
    private string leftDiffText = string.Empty;
    private string rightDiffText = string.Empty;
    private string status = "Select an INI document";
    private CancellationTokenSource? diffCancellation;
    private long operationGeneration;

    public ConfigurationsViewModel(ApplicationSession session, IConfigurationFileRepository files, IConfigurationSnapshotRepository snapshots, ILineDiffService diff)
    {
        this.session = session;
        this.files = files;
        this.snapshots = snapshots;
        this.diff = diff;
        Open = ReactiveCommand.CreateFromTask(OpenAsync).Enhance(text: "Open configuration", name: "OpenConfiguration");
        Reload = ReactiveCommand.CreateFromTask(ReloadAsync).Enhance(text: "Discard and reload", name: "ReloadConfiguration");
        Save = ReactiveCommand.CreateFromTask(SaveAsync).Enhance(text: "Save atomically", name: "SaveConfiguration");
        CaptureSnapshot = ReactiveCommand.CreateFromTask(CaptureSnapshotAsync).Enhance(text: "Capture snapshot", name: "CaptureConfigurationSnapshot");
        ApplySnapshot = ReactiveCommand.Create(ApplySnapshotCore).Enhance(text: "Apply snapshot", name: "ApplyConfigurationSnapshot");
        DeleteSnapshot = ReactiveCommand.CreateFromTask(DeleteSnapshotAsync).Enhance(text: "Delete snapshot", name: "DeleteConfigurationSnapshot");
        ApplyRecovery = ReactiveCommand.CreateFromTask(ApplyRecoveryAsync).Enhance(text: "Load recovery", name: "LoadConfigurationRecovery");
        Compare = ReactiveCommand.CreateFromTask(CompareAsync).Enhance(text: "Compare", name: "CompareConfiguration");
        CancelCompare = ReactiveCommand.Create(() => { diffCancellation?.Cancel(); return Result.Success(); }).Enhance(text: "Cancel diff", name: "CancelConfigurationDiff");
        RefreshDocuments = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.RefreshConfigurationDocumentsAsync(CancellationToken.None))).Enhance(text: "Refresh files", name: "RefreshConfigurationFiles");
    }

    public ObservableCollection<ConfigurationDocumentSummary> Documents => session.ConfigurationDocuments;
    public IEnhancedCommand<Result> Open { get; }
    public IEnhancedCommand<Result> Reload { get; }
    public IEnhancedCommand<Result> Save { get; }
    public IEnhancedCommand<Result> CaptureSnapshot { get; }
    public IEnhancedCommand<Result> ApplySnapshot { get; }
    public IEnhancedCommand<Result> DeleteSnapshot { get; }
    public IEnhancedCommand<Result> ApplyRecovery { get; }
    public IEnhancedCommand<Result> Compare { get; }
    public IEnhancedCommand<Result> CancelCompare { get; }
    public IEnhancedCommand<Result> RefreshDocuments { get; }
    public ConfigurationDocumentSummary? SelectedDocument { get => selectedDocument; set => this.RaiseAndSetIfChanged(ref selectedDocument, value); }
    public string EditorText => editor?.Text ?? string.Empty;
    public bool IsDirty => editor?.IsDirty ?? false;
    public bool HasSnapshot => snapshot is not null;
    public string Format => editor is null ? string.Empty : $"{editor.Baseline.Format.Encoding}, {editor.Baseline.Format.NewLines}";
    public string LeftDiffText { get => leftDiffText; private set => this.RaiseAndSetIfChanged(ref leftDiffText, value); }
    public string RightDiffText { get => rightDiffText; private set => this.RaiseAndSetIfChanged(ref rightDiffText, value); }
    public string Status { get => status; private set => this.RaiseAndSetIfChanged(ref status, value); }

    public void UpdateEditorText(string text)
    {
        if (editor is null || string.Equals(editor.Text, text, StringComparison.Ordinal)) return;
        editor = editor.ReplaceText(text);
        this.RaisePropertyChanged(nameof(IsDirty));
        Status = editor.IsDirty ? "Dirty: editor differs from accepted disk baseline" : "Clean: editor matches disk baseline";
        operationGeneration++;
        diffCancellation?.Cancel();
    }

    public void UpdateSelection(int start, int length)
    {
        if (editor is not null) editor = editor.Select(start, length);
    }

    private async Task<Result> OpenAsync()
    {
        if (SelectedDocument is null) return Result.Failure("Select a configuration file.");
        var generation = ++operationGeneration;
        var loaded = await files.LoadAsync(SelectedDocument.Id, FileLimits, CancellationToken.None);
        if (!loaded.IsSuccess) return Failure(loaded.Error!);
        var saved = await snapshots.FindAsync(SelectedDocument.Id, CancellationToken.None);
        if (!saved.IsSuccess) return Failure(saved.Error!);
        if (generation != operationGeneration) return Result.Success();
        editor = ConfigurationEditorState.Loaded(loaded.Value!);
        snapshot = saved.Value;
        LeftDiffText = RightDiffText = string.Empty;
        RaiseEditorState();
        Status = $"Opened {SelectedDocument.RelativePath}";
        return Result.Success();
    }

    private Task<Result> ReloadAsync() => OpenAsync();

    private async Task<Result> SaveAsync()
    {
        if (editor is null) return Result.Failure("Open a configuration file.");
        var saved = await files.SaveAsync(editor.Baseline.Id, editor.Text, editor.Baseline.Format, editor.Baseline.Revision, CancellationToken.None);
        if (!saved.IsSuccess) return Failure(saved.Error!);
        var receipt = saved.Value!;
        editor = editor.AcceptSave(receipt);
        RaiseEditorState();
        Status = receipt.RecoveryBackupCreated ? "Saved atomically; previous disk bytes retained as recovery backup" : "Saved atomically";
        return Result.Success();
    }

    private async Task<Result> CaptureSnapshotAsync()
    {
        if (editor is null) return Result.Failure("Open a configuration file.");
        var captured = new SavedConfigurationSnapshot(editor.Baseline.Id, editor.Text, editor.Baseline.Format);
        var result = await snapshots.UpsertAsync(captured, CancellationToken.None);
        if (!result.IsSuccess) return Failure(result.Error!);
        snapshot = captured;
        this.RaisePropertyChanged(nameof(HasSnapshot));
        Status = $"Snapshot captured; disk dirty state remains {editor.IsDirty}";
        return Result.Success();
    }

    private Result ApplySnapshotCore()
    {
        if (editor is null || snapshot is null) return Result.Failure("No snapshot is available.");
        editor = editor.ApplySnapshot(snapshot);
        operationGeneration++;
        diffCancellation?.Cancel();
        RaiseEditorState();
        Status = "Snapshot applied to editor; disk baseline is unchanged";
        return Result.Success();
    }

    private async Task<Result> DeleteSnapshotAsync()
    {
        if (editor is null) return Result.Failure("Open a configuration file.");
        var result = await snapshots.RemoveAsync(editor.Baseline.Id, CancellationToken.None);
        if (!result.IsSuccess) return Failure(result.Error!);
        snapshot = null;
        this.RaisePropertyChanged(nameof(HasSnapshot));
        Status = "Snapshot deleted; editor and disk are unchanged";
        return Result.Success();
    }

    private async Task<Result> ApplyRecoveryAsync()
    {
        if (editor is null) return Result.Failure("Open a configuration file.");
        var recovered = await files.LoadRecoveryAsync(editor.Baseline.Id, FileLimits, CancellationToken.None);
        if (!recovered.IsSuccess) return Failure(recovered.Error!);
        if (recovered.Value is null) return Result.Failure("No recovery backup exists.");
        editor = editor.ApplySnapshot(recovered.Value);
        operationGeneration++;
        diffCancellation?.Cancel();
        RaiseEditorState();
        Status = "Recovery bytes loaded into editor; save explicitly to replace disk";
        return Result.Success();
    }

    private async Task<Result> CompareAsync()
    {
        if (editor is null) return Result.Failure("Open a configuration file.");
        diffCancellation?.Cancel();
        diffCancellation?.Dispose();
        diffCancellation = new CancellationTokenSource();
        var generation = ++operationGeneration;
        var left = snapshot?.Text ?? editor.Baseline.Text;
        var result = await diff.CompareAsync(left, editor.Text, LineDiffLimits.Default, diffCancellation.Token);
        if (generation != operationGeneration || result.Error?.Code == "configuration.diff_cancelled") return Result.Success();
        if (!result.IsSuccess) return Failure(result.Error!);
        LeftDiffText = string.Join('\n', result.Value!.Rows.Select(row => row.LeftText ?? string.Empty));
        RightDiffText = string.Join('\n', result.Value.Rows.Select(row => row.RightText ?? string.Empty));
        Status = $"Diff: {result.Value.InsertedCount:N0} inserted, {result.Value.DeletedCount:N0} deleted, {result.Value.UnchangedCount:N0} unchanged";
        return Result.Success();
    }

    private void RaiseEditorState()
    {
        this.RaisePropertyChanged(nameof(EditorText));
        this.RaisePropertyChanged(nameof(IsDirty));
        this.RaisePropertyChanged(nameof(HasSnapshot));
        this.RaisePropertyChanged(nameof(Format));
    }

    private Result Failure(AAML.Application.Common.Error error) { Status = $"{error.Code}: {error.Message}"; return Result.Failure(error.Message); }
    private static Result ToCommand(AAML.Application.Common.Result result) => result.IsSuccess ? Result.Success() : Result.Failure(result.Error!.Message);
    public void Dispose() { diffCancellation?.Cancel(); diffCancellation?.Dispose(); }
}
