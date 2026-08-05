using System.Collections.ObjectModel;
using System.ComponentModel;
using CSharpFunctionalExtensions;
using ReactiveUI;
using AAML.Domain.Mods;
using Zafiro.UI.Commands;
using Zafiro.UI.Shell.Utils;
using AAML.Application.Mods.Workshop;
using AAML.Application.Mods.Grid;
using AAML.Application.Ports;
using AAML.Application.Settings;

namespace AAML.Avalonia;

[Section("mods", "fa-table-list", 1, FriendlyName = "Mods")]
public sealed class ModsViewModel : ReactiveObject, IDisposable
{
    private readonly ApplicationSession session;
    private string searchText = string.Empty;
    private bool groupByCategory;
    private SessionModRow? selectedRow;
    private string manualName = string.Empty;
    private string note = string.Empty;
    private bool isHidden;
    private Category? selectedCategory;
    private Tag? selectedTag;
    private string tagNames = string.Empty;
    private string taxonomyName = string.Empty;
    private readonly HashSet<ModKey> selectedKeys = [];
    private CancellationTokenSource? workshopCancellation;
    private CancellationTokenSource? selectedDetailsCancellation;
    private string workshopSummary = "Workshop state not refreshed";
    private double? workshopProgress;
    private bool isMonitoringWorkshop;
    private bool includeHidden = true;
    private StateFilterOption? selectedStateFilter;
    private SessionDependencyRelationship? selectedDependency;
    private string onlineDetails = string.Empty;
    private string selectedPreviewImagePath = string.Empty;
    private string tagColor = string.Empty;
    private ModRemovalPreview? removalPreview;
    private ModKey? removalPreviewKey;
    private string removalSummary = string.Empty;
    private bool confirmUnsubscribeRetain;
    private bool confirmRemoveRetainedIntent;
    private bool confirmManualDeletion;
    private bool isInspectorVisible = true;
    private bool gridPreferencesLoaded;

    private readonly IExternalLauncher externalLauncher;

    public ModsViewModel(ApplicationSession session, IExternalLauncher externalLauncher)
    {
        this.session = session;
        this.externalLauncher = externalLauncher;
        LoadGridPreferences();
        session.PropertyChanged += OnSessionPropertyChanged;
        Refresh = ReactiveCommand.CreateFromTask(async () => (await session.RefreshModsAsync(CancellationToken.None)).IsSuccess ? Result.Success() : Result.Failure(session.Status)).Enhance(text: "Refresh mods", name: "RefreshMods");
        Save = ReactiveCommand.CreateFromTask(async () => (await session.SaveModDraftsAsync(CancellationToken.None)).IsSuccess ? Result.Success() : Result.Failure(session.Status)).Enhance(text: "Save mod state", name: "SaveModState");
        SaveMetadata = ReactiveCommand.CreateFromTask(SaveMetadataAsync).Enhance(text: "Save metadata", name: "SaveMetadata");
        BulkCategory = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.AssignCategoryAsync(SelectedKeys(), SelectedCategory?.Id, CancellationToken.None))).Enhance(text: "Assign category", name: "BulkCategory");
        BulkTags = ReactiveCommand.CreateFromTask(BulkTagsAsync).Enhance(text: "Add tags", name: "BulkTags");
        BulkRemoveTags = ReactiveCommand.CreateFromTask(BulkRemoveTagsAsync).Enhance(text: "Remove tags", name: "BulkRemoveTags");
        ClearCategory = ReactiveCommand.Create(() => { SelectedCategory = null; return Result.Success(); }).Enhance(text: "Clear category", name: "ClearCategory");
        ClearFocusedMods = ReactiveCommand.Create(() => { session.ClearFocusedMods(); return Result.Success(); }).Enhance(text: "Clear conflict filter", name: "ClearFocusedMods");
        DiscardActivation = ReactiveCommand.Create(() => { session.CancelAutoSaveOwner("mods"); session.DiscardModDrafts(); return Result.Success(); }).Enhance(text: "Discard activation edits", name: "DiscardActivation");
        RefreshWorkshop = ReactiveCommand.CreateFromTask(RefreshWorkshopAsync).Enhance(text: "Refresh Workshop", name: "RefreshWorkshop");
        UpdateSelected = ReactiveCommand.CreateFromTask(UpdateSelectedAsync).Enhance(text: "Update selected", name: "UpdateSelectedMods");
        StopMonitoring = ReactiveCommand.Create(() => { workshopCancellation?.Cancel(); return Result.Success(); }).Enhance(text: "Stop monitoring", name: "StopWorkshopMonitoring");
        PreferDuplicate = ReactiveCommand.CreateFromTask(async () => SelectedRow?.Key is { } key ? ToCommand(await session.PreferDuplicateAsync(key, CancellationToken.None)) : Result.Failure("Select a mod.")).Enhance(text: "Prefer installation", name: "PreferDuplicate");
        ClearDuplicatePreference = ReactiveCommand.CreateFromTask(async () => SelectedRow?.Key is { } key ? ToCommand(await session.ClearDuplicatePreferenceAsync(key, CancellationToken.None)) : Result.Failure("Select a mod.")).Enhance(text: "Clear duplicate preference", name: "ClearDuplicatePreference");
        ShowDuplicateGroup = ReactiveCommand.Create(() => SelectedRow?.Key is { } key ? ToCommand(session.FocusDuplicateGroup(key)) : Result.Failure("Select a mod.")).Enhance(text: "Show duplicate group", name: "ShowDuplicateGroup");
        ActivateSelected = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.SetSelectedActiveAndSaveAsync(selectedKeys, true, CancellationToken.None))).Enhance(text: "Activate selected", name: "ActivateSelected");
        DeactivateSelected = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.SetSelectedActiveAndSaveAsync(selectedKeys, false, CancellationToken.None))).Enhance(text: "Deactivate selected", name: "DeactivateSelected");
        MoveUp = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.MoveSelectedAndSaveAsync(selectedKeys, -1, CancellationToken.None))).Enhance(text: "Move up", name: "MoveSelectedUp");
        MoveDown = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.MoveSelectedAndSaveAsync(selectedKeys, 1, CancellationToken.None))).Enhance(text: "Move down", name: "MoveSelectedDown");
        Renumber = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.RenumberModsAndSaveAsync(CancellationToken.None))).Enhance(text: "Renumber", name: "RenumberMods");
        HideSelected = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.SetHiddenAsync(selectedKeys, true, CancellationToken.None))).Enhance(text: "Hide selected", name: "HideSelected");
        UnhideSelected = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.SetHiddenAsync(selectedKeys, false, CancellationToken.None))).Enhance(text: "Unhide selected", name: "UnhideSelected");
        LoadDependencies = ReactiveCommand.CreateFromTask(LoadDependenciesAsync).Enhance(text: "Load dependencies", name: "LoadDependencies");
        ActivateDependency = ReactiveCommand.Create(() => SelectedDependency is null ? Result.Failure("Select a dependency.") : ToCommand(session.ActivateDependency(SelectedDependency.WorkshopId))).Enhance(text: "Activate dependency", name: "ActivateDependency");
        ToggleDependencyIgnored = ReactiveCommand.CreateFromTask(ToggleDependencyIgnoredAsync).Enhance(text: "Toggle ignored", name: "ToggleDependencyIgnored");
        ShowDependency = ReactiveCommand.Create(() => SelectedDependency is null ? Result.Failure("Select a dependency.") : ToCommand(session.FocusDependency(SelectedDependency.WorkshopId))).Enhance(text: "Show dependency", name: "ShowDependency");
        OpenModFolder = ReactiveCommand.CreateFromTask(async () => SelectedRow is null ? Result.Failure("Select a mod.") : ToCommand(await externalLauncher.OpenDirectoryAsync(SelectedRow.Location, CancellationToken.None))).Enhance(text: "Open mod folder", name: "OpenModFolder");
        OpenReadme = ReactiveCommand.CreateFromTask(async () => string.IsNullOrWhiteSpace(SelectedRow?.ReadmePath) ? Result.Failure("No README was found.") : ToCommand(await externalLauncher.OpenFileAsync(SelectedRow.ReadmePath, CancellationToken.None))).Enhance(text: "Open README", name: "OpenReadme");
        OpenWorkshop = ReactiveCommand.CreateFromTask(async () => Uri.TryCreate(SelectedRow?.WorkshopUrl, UriKind.Absolute, out var uri) ? ToCommand(await externalLauncher.OpenUriAsync(uri, CancellationToken.None)) : Result.Failure("This mod has no Workshop page.")).Enhance(text: "Open Workshop page", name: "OpenWorkshop");
        OpenChangelog = ReactiveCommand.CreateFromTask(async () => SelectedRow?.WorkshopId is { } id ? ToCommand(await externalLauncher.OpenUriAsync(new Uri($"https://steamcommunity.com/sharedfiles/filedetails/changelog/{id.Value}"), CancellationToken.None)) : Result.Failure("This mod has no Workshop changelog.")).Enhance(text: "Open changelog", name: "OpenChangelog");
        LoadOnlineDetails = ReactiveCommand.CreateFromTask(LoadOnlineDetailsAsync).Enhance(text: "Load Steam details", name: "LoadSteamDetails");
        SaveTagColor = ReactiveCommand.CreateFromTask(async () => SelectedTag is null ? Result.Failure("Select a tag.") : ToCommand(await session.SetTagColorAsync(SelectedTag.Id, TagColor, CancellationToken.None))).Enhance(text: "Save tag color", name: "SaveTagColor");
        SaveView = ReactiveCommand.CreateFromTask(async () => ToCommand(await session.SaveModGridPreferencesAsync(CancellationToken.None))).Enhance(text: "Save mod view", name: "SaveModView");
        AdoptDescriptorTaxonomy = ReactiveCommand.CreateFromTask(async () => SelectedRow?.Key is { } key ? ToCommand(await session.AdoptDescriptorTaxonomyAsync(key, CancellationToken.None)) : Result.Failure("Select a mod.")).Enhance(text: "Adopt descriptor taxonomy", name: "AdoptDescriptorTaxonomy");
        AdoptWorkshopTags = ReactiveCommand.CreateFromTask(async () => SelectedRow?.Key is { } key ? ToCommand(await session.AdoptWorkshopTagsAsync(key, CancellationToken.None)) : Result.Failure("Select a mod.")).Enhance(text: "Adopt Workshop tags", name: "AdoptWorkshopTags");
        SubscribeMissing = ReactiveCommand.CreateFromTask(async () => SelectedRow?.WorkshopId is { } id ? ToCommand(await session.SubscribeRetainedAsync(id, CancellationToken.None)) : Result.Failure("Select a retained Workshop item.")).Enhance(text: "Subscribe missing item", name: "SubscribeMissing");
        UnsubscribeRetain = ReactiveCommand.CreateFromTask(async () => ConfirmUnsubscribeRetain ? ToCommand(await session.UnsubscribeRetainingIntentAsync(selectedKeys, CancellationToken.None)) : Result.Failure("Confirm unsubscribe and retain before continuing.")).Enhance(text: "Unsubscribe and retain", name: "UnsubscribeRetain");
        RemoveRetainedIntent = ReactiveCommand.CreateFromTask(async () => ConfirmRemoveRetainedIntent && SelectedRow?.WorkshopId is { } id ? ToCommand(await session.RemoveRetainedIntentAsync(id, CancellationToken.None)) : Result.Failure("Confirm removal of the retained intent before continuing.")).Enhance(text: "Remove retained intent", name: "RemoveRetainedIntent");
        PreviewRemoval = ReactiveCommand.CreateFromTask(PreviewRemovalAsync).Enhance(text: "Preview manual removal", name: "PreviewManualRemoval");
        ConfirmRemoval = ReactiveCommand.CreateFromTask(ConfirmRemovalAsync).Enhance(text: "Confirm manual removal", name: "ConfirmManualRemoval");
        ToggleGroup = ReactiveCommand.Create(() => SelectedRow?.GroupKey is { } key ? Toggle(key) : Result.Failure("Select a group row.")).Enhance(text: "Expand/collapse group", name: "ToggleModGroup");
        CreateCategory = ReactiveCommand.CreateFromTask(async () => await RunTaxonomyAsync(() => session.CreateCategoryAsync(TaxonomyName, CancellationToken.None))).Enhance(text: "Create category", name: "CreateCategory");
        RenameCategory = ReactiveCommand.CreateFromTask(async () => SelectedCategory is null ? Result.Failure("Select a category.") : await RunTaxonomyAsync(() => session.RenameCategoryAsync(SelectedCategory.Id, TaxonomyName, CancellationToken.None))).Enhance(text: "Rename category", name: "RenameCategory");
        MoveCategoryUp = ReactiveCommand.CreateFromTask(async () => SelectedCategory is null ? Result.Failure("Select a category.") : await RunTaxonomyAsync(() => session.ReorderCategoryAsync(SelectedCategory.Id, Math.Max(0, SelectedCategory.Order - 1), CancellationToken.None))).Enhance(text: "Move category up", name: "MoveCategoryUp");
        MoveCategoryDown = ReactiveCommand.CreateFromTask(async () => SelectedCategory is null ? Result.Failure("Select a category.") : await RunTaxonomyAsync(() => session.ReorderCategoryAsync(SelectedCategory.Id, Math.Min(Categories.Count - 1, SelectedCategory.Order + 1), CancellationToken.None))).Enhance(text: "Move category down", name: "MoveCategoryDown");
        DeleteCategory = ReactiveCommand.CreateFromTask(async () => SelectedCategory is null ? Result.Failure("Select a category.") : await RunTaxonomyAsync(() => session.DeleteCategoryAsync(SelectedCategory.Id, CancellationToken.None))).Enhance(text: "Delete category", name: "DeleteCategory");
        CreateTag = ReactiveCommand.CreateFromTask(async () => await RunTaxonomyAsync(() => session.CreateTagAsync(TaxonomyName, CancellationToken.None))).Enhance(text: "Create tag", name: "CreateTag");
        RenameTag = ReactiveCommand.CreateFromTask(async () => SelectedTag is null ? Result.Failure("Select a tag.") : await RunTaxonomyAsync(() => session.RenameTagAsync(SelectedTag.Id, TaxonomyName, CancellationToken.None))).Enhance(text: "Rename tag", name: "RenameTag");
        DeleteTag = ReactiveCommand.CreateFromTask(async () => SelectedTag is null ? Result.Failure("Select a tag.") : await RunTaxonomyAsync(() => session.DeleteTagAsync(SelectedTag.Id, CancellationToken.None))).Enhance(text: "Delete tag", name: "DeleteTag");
        RefreshTaxonomy();
    }

    public ObservableCollection<SessionModRow> Rows => session.ModRows;
    public string Status => session.Status;
    public int UnsavedModDraftCount => session.UnsavedModDraftCount;
    public bool HasUnsaved => session.HasUnsavedModDrafts;
    public string AutoSaveStatus => session.Settings?.AutoSaveChanges == true ? "Auto-save on" : "Auto-save off";
    public IEnhancedCommand<Result> Refresh { get; }
    public IEnhancedCommand<Result> Save { get; }
    public IEnhancedCommand<Result> SaveMetadata { get; }
    public IEnhancedCommand<Result> BulkCategory { get; }
    public IEnhancedCommand<Result> BulkTags { get; }
    public IEnhancedCommand<Result> BulkRemoveTags { get; }
    public IEnhancedCommand<Result> ClearCategory { get; }
    public IEnhancedCommand<Result> ClearFocusedMods { get; }
    public IEnhancedCommand<Result> DiscardActivation { get; }
    public IEnhancedCommand<Result> RefreshWorkshop { get; }
    public IEnhancedCommand<Result> UpdateSelected { get; }
    public IEnhancedCommand<Result> StopMonitoring { get; }
    public IEnhancedCommand<Result> PreferDuplicate { get; }
    public IEnhancedCommand<Result> ClearDuplicatePreference { get; }
    public IEnhancedCommand<Result> ShowDuplicateGroup { get; }
    public IEnhancedCommand<Result> ActivateSelected { get; }
    public IEnhancedCommand<Result> DeactivateSelected { get; }
    public IEnhancedCommand<Result> MoveUp { get; }
    public IEnhancedCommand<Result> MoveDown { get; }
    public IEnhancedCommand<Result> Renumber { get; }
    public IEnhancedCommand<Result> HideSelected { get; }
    public IEnhancedCommand<Result> UnhideSelected { get; }
    public IEnhancedCommand<Result> LoadDependencies { get; }
    public IEnhancedCommand<Result> ActivateDependency { get; }
    public IEnhancedCommand<Result> ToggleDependencyIgnored { get; }
    public IEnhancedCommand<Result> ShowDependency { get; }
    public IEnhancedCommand<Result> OpenModFolder { get; }
    public IEnhancedCommand<Result> OpenReadme { get; }
    public IEnhancedCommand<Result> OpenWorkshop { get; }
    public IEnhancedCommand<Result> OpenChangelog { get; }
    public IEnhancedCommand<Result> LoadOnlineDetails { get; }
    public string OnlineDetails { get => onlineDetails; private set => this.RaiseAndSetIfChanged(ref onlineDetails, value); }
    public string SelectedPreviewImagePath { get => selectedPreviewImagePath; private set => this.RaiseAndSetIfChanged(ref selectedPreviewImagePath, value); }
    public string TagColor { get => tagColor; set => this.RaiseAndSetIfChanged(ref tagColor, value); }
    public IEnhancedCommand<Result> SaveTagColor { get; }
    public IEnhancedCommand<Result> SaveView { get; }
    public IEnhancedCommand<Result> AdoptDescriptorTaxonomy { get; }
    public IEnhancedCommand<Result> AdoptWorkshopTags { get; }
    public IEnhancedCommand<Result> SubscribeMissing { get; }
    public IEnhancedCommand<Result> UnsubscribeRetain { get; }
    public IEnhancedCommand<Result> RemoveRetainedIntent { get; }
    public IEnhancedCommand<Result> PreviewRemoval { get; }
    public IEnhancedCommand<Result> ConfirmRemoval { get; }
    public string RemovalSummary { get => removalSummary; private set => this.RaiseAndSetIfChanged(ref removalSummary, value); }
    public IEnhancedCommand<Result> ToggleGroup { get; }
    public bool IsInspectorVisible { get => isInspectorVisible; set => this.RaiseAndSetIfChanged(ref isInspectorVisible, value); }
    public ObservableCollection<SessionDependencyRelationship> RequiredDependencies { get; } = [];
    public ObservableCollection<SessionDependencyRelationship> Dependents { get; } = [];
    public SessionDependencyRelationship? SelectedDependency { get => selectedDependency; set => this.RaiseAndSetIfChanged(ref selectedDependency, value); }
    public bool HasFocusedMods => session.HasFocusedMods;
    public string WorkshopSummary { get => workshopSummary; private set => this.RaiseAndSetIfChanged(ref workshopSummary, value); }
    public double? WorkshopProgress { get => workshopProgress; private set => this.RaiseAndSetIfChanged(ref workshopProgress, value); }
    public bool IsMonitoringWorkshop { get => isMonitoringWorkshop; private set => this.RaiseAndSetIfChanged(ref isMonitoringWorkshop, value); }
    public bool IsWorkshopBusy => session.IsWorkshopBusy;
    public WorkshopAvailability WorkshopAvailability => session.WorkshopAvailability;
    public string WorkshopConnectionDisplay => WorkshopAvailability.State switch
    {
        WorkshopConnectionState.Connected => "Workshop connected",
        WorkshopConnectionState.Connecting => "Connecting to Workshop...",
        WorkshopConnectionState.Unavailable => string.IsNullOrWhiteSpace(WorkshopAvailability.Error)
            ? "Workshop unavailable"
            : $"Workshop unavailable: {WorkshopAvailability.Error}",
        _ => "Workshop connection not checked"
    };
    public bool CanRefreshWorkshop => !IsWorkshopBusy;
    public bool CanUpdateSelected => !IsWorkshopBusy && selectedKeys.Any(key => session.DiscoveredMods.Any(mod => mod.Key == key && mod.WorkshopId.HasValue));
    public bool CanSubscribeMissing => !IsWorkshopBusy && SelectedRow?.IsRetainedMissing == true;
    public bool CanUnsubscribeRetain => !IsWorkshopBusy && selectedKeys.Any(key => session.DiscoveredMods.Any(mod => mod.Key == key && mod.WorkshopId.HasValue));
    public bool CanRemoveRetainedIntent => !IsWorkshopBusy && SelectedRow?.IsRetainedMissing == true;
    public bool CanLoadOnlineDetails => SelectedRow?.WorkshopId.HasValue == true && !SelectedRow.IsRetainedMissing;
    public bool CanLoadDependencies => CanLoadOnlineDetails;
    public bool CanPreviewRemoval => SelectedRow?.Key is { Source: ModSource.Manual };
    public bool CanConfirmRemoval => ConfirmManualDeletion && removalPreview is not null && removalPreviewKey == SelectedRow?.Key;
    public bool ConfirmUnsubscribeRetain { get => confirmUnsubscribeRetain; set => this.RaiseAndSetIfChanged(ref confirmUnsubscribeRetain, value); }
    public bool ConfirmRemoveRetainedIntent { get => confirmRemoveRetainedIntent; set => this.RaiseAndSetIfChanged(ref confirmRemoveRetainedIntent, value); }
    public bool ConfirmManualDeletion
    {
        get => confirmManualDeletion;
        set
        {
            this.RaiseAndSetIfChanged(ref confirmManualDeletion, value);
            this.RaisePropertyChanged(nameof(CanConfirmRemoval));
        }
    }
    public bool IncludeHidden { get => includeHidden; set { if (includeHidden == value) return; this.RaiseAndSetIfChanged(ref includeHidden, value); ApplyFilter(); } }
    public StateFilterOption? SelectedStateFilter { get => selectedStateFilter; set { if (selectedStateFilter == value) return; this.RaiseAndSetIfChanged(ref selectedStateFilter, value); ApplyFilter(); } }
    public IReadOnlyList<StateFilterOption> StateFilters { get; } = [new("All states", null), .. Enum.GetValues<ModGridSemanticState>().Select(state => new StateFilterOption(state.ToString(), state))];
    public IEnhancedCommand<Result> CreateCategory { get; }
    public IEnhancedCommand<Result> RenameCategory { get; }
    public IEnhancedCommand<Result> MoveCategoryUp { get; }
    public IEnhancedCommand<Result> MoveCategoryDown { get; }
    public IEnhancedCommand<Result> DeleteCategory { get; }
    public IEnhancedCommand<Result> CreateTag { get; }
    public IEnhancedCommand<Result> RenameTag { get; }
    public IEnhancedCommand<Result> DeleteTag { get; }
    public ObservableCollection<Category> Categories { get; } = [];
    public ObservableCollection<Tag> Tags { get; } = [];
    public SessionModRow? SelectedRow { get => selectedRow; private set => this.RaiseAndSetIfChanged(ref selectedRow, value); }
    public int SelectedCount => selectedKeys.Count;
    public bool HasSelection => SelectedCount > 0;
    public string ManualName { get => manualName; set => this.RaiseAndSetIfChanged(ref manualName, value); }
    public string Note { get => note; set => this.RaiseAndSetIfChanged(ref note, value); }
    public bool IsHidden { get => isHidden; set => this.RaiseAndSetIfChanged(ref isHidden, value); }
    public Category? SelectedCategory { get => selectedCategory; set => this.RaiseAndSetIfChanged(ref selectedCategory, value); }
    public Tag? SelectedTag { get => selectedTag; set => this.RaiseAndSetIfChanged(ref selectedTag, value); }
    public string TagNames { get => tagNames; set => this.RaiseAndSetIfChanged(ref tagNames, value); }
    public string TaxonomyName { get => taxonomyName; set => this.RaiseAndSetIfChanged(ref taxonomyName, value); }
    public string SearchText
    {
        get => searchText;
        set { this.RaiseAndSetIfChanged(ref searchText, value); session.ProjectMods(value, GroupByCategory); }
    }
    public bool GroupByCategory
    {
        get => groupByCategory;
        set { if (groupByCategory == value) return; this.RaiseAndSetIfChanged(ref groupByCategory, value); session.SetModGrouping(value); }
    }

    public void Activate() => session.ActivateAutoSaveOwner("mods");

    public void SetSelection(IEnumerable<SessionModRow> rows)
    {
        var selected = rows.Where(row => row.Key.HasValue).ToArray();
        selectedKeys.Clear();
        foreach (var row in selected) selectedKeys.Add(row.Key!.Value);
        SelectedRow = rows.FirstOrDefault();
        SelectedPreviewImagePath = SelectedRow?.PreviewImagePath ?? string.Empty;
        OnlineDetails = string.Empty;
        RequiredDependencies.Clear();
        Dependents.Clear();
        SelectedDependency = null;
        removalPreview = null;
        removalPreviewKey = null;
        RemovalSummary = string.Empty;
        ConfirmUnsubscribeRetain = false;
        ConfirmRemoveRetainedIntent = false;
        ConfirmManualDeletion = false;
        LoadSelectedMetadata();
        selectedDetailsCancellation?.Cancel();
        selectedDetailsCancellation?.Dispose();
        selectedDetailsCancellation = null;
        if (SelectedRow?.Key is { } selectedKey && SelectedRow.WorkshopId.HasValue)
        {
            selectedDetailsCancellation = new CancellationTokenSource();
            _ = LoadOnlineDetailsForSelectionAsync(selectedKey, selectedDetailsCancellation.Token);
        }
        this.RaisePropertyChanged(nameof(SelectedCount));
        this.RaisePropertyChanged(nameof(HasSelection));
        RaiseSelectionCommandState();
    }

    private async Task<Result> SaveMetadataAsync()
    {
        if (SelectedRow?.Key is not { } key) return Result.Failure("Select one mod.");
        var parsed = ParseTags();
        if (!parsed.IsSuccess) return Result.Failure(parsed.Error);
        var result = await session.SaveMetadataAsync(new SessionModMetadata(key, ManualName, Note, IsHidden, SelectedCategory?.Id, parsed.Value!), CancellationToken.None);
        if (result.IsSuccess) LoadSelectedMetadata();
        return ToCommand(result);
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        this.RaisePropertyChanged(nameof(Status));
        if (args.PropertyName is nameof(ApplicationSession.UnsavedModDraftCount) or nameof(ApplicationSession.HasUnsavedModDrafts)) { this.RaisePropertyChanged(nameof(UnsavedModDraftCount)); this.RaisePropertyChanged(nameof(HasUnsaved)); }
        if (args.PropertyName == nameof(ApplicationSession.Settings))
        {
            RefreshTaxonomy();
            if (!gridPreferencesLoaded) LoadGridPreferences();
            this.RaisePropertyChanged(nameof(AutoSaveStatus));
        }
        if (args.PropertyName == nameof(ApplicationSession.HasFocusedMods)) this.RaisePropertyChanged(nameof(HasFocusedMods));
        if (args.PropertyName is not (nameof(ApplicationSession.IsWorkshopBusy) or nameof(ApplicationSession.WorkshopAvailability))) return;
        this.RaisePropertyChanged(nameof(IsWorkshopBusy));
        this.RaisePropertyChanged(nameof(WorkshopAvailability));
        this.RaisePropertyChanged(nameof(WorkshopConnectionDisplay));
        this.RaisePropertyChanged(nameof(CanRefreshWorkshop));
        if (session.WorkshopAvailability.State == WorkshopConnectionState.Connected)
            WorkshopSummary = $"Workshop state checked {session.WorkshopAvailability.LastCheckedAt?.LocalDateTime:g}";
        else if (session.WorkshopAvailability.State == WorkshopConnectionState.Unavailable)
            WorkshopSummary = session.WorkshopAvailability.Error ?? "Workshop state check failed";
        RaiseSelectionCommandState();
    }

    private async Task<Result> BulkTagsAsync()
    {
        var parsed = ParseTags();
        if (!parsed.IsSuccess) return Result.Failure(parsed.Error);
        return ToCommand(await session.AddTagsAsync(SelectedKeys(), parsed.Value!, CancellationToken.None));
    }

    private async Task<Result> BulkRemoveTagsAsync()
    {
        var parsed = ParseTags();
        if (!parsed.IsSuccess) return Result.Failure(parsed.Error);
        return ToCommand(await session.RemoveTagsAsync(SelectedKeys(), parsed.Value!, CancellationToken.None));
    }

    private async Task<Result> RefreshWorkshopAsync()
    {
        var progress = new Progress<WorkshopOperationProgress>(UpdateWorkshopProgress);
        var result = await session.RefreshWorkshopStatesAsync(progress, CancellationToken.None);
        WorkshopSummary = session.Status;
        WorkshopProgress = null;
        return ToCommand(result);
    }

    private async Task<Result> UpdateSelectedAsync()
    {
        workshopCancellation?.Cancel();
        workshopCancellation?.Dispose();
        workshopCancellation = new CancellationTokenSource();
        IsMonitoringWorkshop = true;
        try
        {
            var result = await session.DownloadWorkshopUpdatesAsync(selectedKeys, new Progress<WorkshopOperationProgress>(UpdateWorkshopProgress), workshopCancellation.Token);
            WorkshopSummary = session.Status;
            return ToCommand(result);
        }
        finally
        {
            IsMonitoringWorkshop = false;
            WorkshopProgress = null;
        }
    }

    private void UpdateWorkshopProgress(WorkshopOperationProgress progress)
    {
        WorkshopProgress = progress.BytesTotal is > 0 ? (double)progress.BytesDownloaded / progress.BytesTotal.Value * 100 : null;
        WorkshopSummary = progress.Operation == "workshop.refresh" ? $"Workshop state: {progress.CompletedItems:N0}/{progress.TotalItems:N0}" : progress.BytesTotal is > 0 ? $"Downloading: {WorkshopProgress:N0}%" : $"Monitoring: {progress.CompletedItems:N0}/{progress.TotalItems:N0}";
    }

    private void ApplyFilter() => session.SetModGridFilter(IncludeHidden, SelectedStateFilter?.State);
    private Result Toggle(ModGridGroupKey key) { session.ToggleModGroup(key); return Result.Success(); }

    private async Task<Result> LoadDependenciesAsync()
    {
        if (SelectedRow?.Key is not { } key) return Result.Failure("Select a Workshop mod.");
        var result = await session.LoadDependencyDetailsAsync(key, CancellationToken.None);
        if (!result.IsSuccess) return Result.Failure(result.Error!.Message);
        RequiredDependencies.Clear(); foreach (var item in result.Value!.Required) RequiredDependencies.Add(item);
        Dependents.Clear(); foreach (var item in result.Value.Dependents) Dependents.Add(item);
        return Result.Success();
    }

    private async Task<Result> ToggleDependencyIgnoredAsync()
    {
        if (SelectedRow?.Key is not { } parent || SelectedDependency is null) return Result.Failure("Select a direct required dependency.");
        var result = await session.SetDependencyIgnoredAsync(parent, SelectedDependency.WorkshopId, !SelectedDependency.IsIgnored, CancellationToken.None);
        if (!result.IsSuccess) return ToCommand(result);
        return await LoadDependenciesAsync();
    }

    private async Task<Result> LoadOnlineDetailsAsync()
    {
        if (SelectedRow?.Key is not { } key) return Result.Failure("Select a Workshop mod.");
        return await LoadOnlineDetailsForSelectionAsync(key, CancellationToken.None);
    }

    private async Task<Result> LoadOnlineDetailsForSelectionAsync(ModKey key, CancellationToken cancellationToken)
    {
        var result = await session.LoadWorkshopDetailsAsync(key, cancellationToken);
        if (cancellationToken.IsCancellationRequested) return Result.Success();
        if (!result.IsSuccess) return Result.Failure(result.Error!.Message);
        var details = result.Value!;
        if (SelectedRow?.Key == key && !string.IsNullOrWhiteSpace(details.PreviewImagePath)) SelectedPreviewImagePath = details.PreviewImagePath;
        OnlineDetails = $"Author: {details.Author ?? details.OwnerSteamId?.ToString() ?? "Unknown"}\nCreated: {details.CreatedAt:g}\nUpdated: {details.UpdatedAt:g}\nTags: {string.Join(", ", details.Tags)}\n\n{details.Description}";
        return Result.Success();
    }

    private async Task<Result> PreviewRemovalAsync()
    {
        if (SelectedRow?.Key is not { } key) return Result.Failure("Select a manual mod.");
        var result = await session.PreviewManualRemovalAsync(key, CancellationToken.None); if (!result.IsSuccess) return Result.Failure(result.Error!.Message);
        removalPreview = result.Value; removalPreviewKey = key; RemovalSummary = $"Confirm deletion of {result.Value!.FileCount:N0} files ({result.Value.TotalBytes:N0} bytes): {string.Join(", ", result.Value.SampleFiles)}"; this.RaisePropertyChanged(nameof(CanConfirmRemoval)); return Result.Success();
    }

    private async Task<Result> ConfirmRemovalAsync()
    {
        if (removalPreview is null || removalPreviewKey != SelectedRow?.Key) return Result.Failure("Preview the manual removal for the current selection first.");
        var result = await session.ConfirmManualRemovalAsync(removalPreview, CancellationToken.None); removalPreview = null; RemovalSummary = string.Empty; return ToCommand(result);
    }

    private async Task<Result> RunTaxonomyAsync(Func<Task<AAML.Application.Common.Result>> action)
    {
        var result = await action();
        if (result.IsSuccess) { RefreshTaxonomy(); LoadSelectedMetadata(); }
        return ToCommand(result);
    }

    private void LoadSelectedMetadata()
    {
        if (SelectedRow?.Key is not { } key || session.GetMetadata(key) is not { } metadata) return;
        ManualName = metadata.ManualName ?? string.Empty;
        Note = metadata.Note ?? string.Empty;
        IsHidden = metadata.IsHidden;
        SelectedCategory = Categories.FirstOrDefault(category => category.Id == metadata.Category);
        TagNames = string.Join(", ", Tags.Where(tag => metadata.Tags.Contains(tag.Id)).Select(tag => tag.Name));
    }

    private void RefreshTaxonomy()
    {
        Categories.Clear();
        foreach (var category in session.Categories.OrderBy(category => category.Order)) Categories.Add(category);
        Tags.Clear();
        foreach (var tag in session.Tags.OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)) Tags.Add(tag);
    }

    private void LoadGridPreferences()
    {
        if (session.Settings is null) return;
        var grid = session.Settings.ModGrid ?? ModGridPreferences.Default;
        includeHidden = grid.IncludeHidden;
        selectedStateFilter = StateFilters.FirstOrDefault(option => option.State == grid.StateFilter);
        groupByCategory = grid.GroupByCategory;
        gridPreferencesLoaded = true;
        this.RaisePropertyChanged(nameof(IncludeHidden));
        this.RaisePropertyChanged(nameof(SelectedStateFilter));
        this.RaisePropertyChanged(nameof(GroupByCategory));
    }

    private CSharpFunctionalExtensions.Result<HashSet<TagId>, string> ParseTags()
    {
        var names = TagNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = new HashSet<TagId>();
        foreach (var name in names)
        {
            var tag = Tags.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (tag is null) return CSharpFunctionalExtensions.Result.Failure<HashSet<TagId>, string>($"Unknown tag: {name}");
            selected.Add(tag.Id);
        }
        return CSharpFunctionalExtensions.Result.Success<HashSet<TagId>, string>(selected);
    }

    private ModKey[] SelectedKeys() => selectedKeys.ToArray();
    private void RaiseSelectionCommandState()
    {
        foreach (var property in new[] { nameof(CanUpdateSelected), nameof(CanSubscribeMissing), nameof(CanUnsubscribeRetain), nameof(CanRemoveRetainedIntent), nameof(CanLoadOnlineDetails), nameof(CanLoadDependencies), nameof(CanPreviewRemoval), nameof(CanConfirmRemoval) })
            this.RaisePropertyChanged(property);
    }
    private static Result ToCommand(AAML.Application.Common.Result result) => result.IsSuccess ? Result.Success() : Result.Failure(result.Error!.Message);
    private static Result ToCommand<T>(AAML.Application.Common.Result<T> result) => result.IsSuccess ? Result.Success() : Result.Failure(result.Error!.Message);
    public void Dispose() { session.PropertyChanged -= OnSessionPropertyChanged; session.CancelAutoSaveOwner("mods"); workshopCancellation?.Cancel(); workshopCancellation?.Dispose(); selectedDetailsCancellation?.Cancel(); selectedDetailsCancellation?.Dispose(); }
}

public sealed record StateFilterOption(string Name, ModGridSemanticState? State);
