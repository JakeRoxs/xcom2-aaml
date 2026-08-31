using System.Collections.ObjectModel;
using AAML.Application.Common;
using AAML.Application.Mods.Grid;
using AAML.Application.Mods;
using AAML.Application.Mods.Dependencies;
using AAML.Application.Mods.Metadata;
using AAML.Application.Mods.Conflicts;
using AAML.Application.Configurations;
using AAML.Application.Mods.Workshop;
using AAML.Application.Mods.Duplicates;
using AAML.Application.Profiles;
using AAML.Application.Launching;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Application.Startup;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Domain.Profiles;
using ReactiveUI;
using AAML.Application.Updates;
using AAML.Application.Diagnostics;
using AAML.Application.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace AAML.Avalonia;

public sealed class ApplicationSession(IServiceProvider serviceProvider) : ReactiveObject, IDisposable
{
    private readonly ISettingsBootstrapper bootstrapper = serviceProvider.GetRequiredService<ISettingsBootstrapper>();
    private readonly IModCatalogSource catalog = serviceProvider.GetRequiredService<IModCatalogSource>();
    private readonly IGameLaunchCoordinator launchCoordinator = serviceProvider.GetRequiredService<IGameLaunchCoordinator>();
    private readonly IGameConfigurationWriter configurationWriter = serviceProvider.GetRequiredService<IGameConfigurationWriter>();
    private readonly ISteamSettingsIntegrator steamSettings = serviceProvider.GetRequiredService<ISteamSettingsIntegrator>();
    private readonly IModIntentService modIntents = serviceProvider.GetRequiredService<IModIntentService>();
    private readonly IProfileService profileService = serviceProvider.GetRequiredService<IProfileService>();
    private readonly IProfileInterchange profileInterchange = serviceProvider.GetRequiredService<IProfileInterchange>();
    private readonly ILegacyProfileImportService legacyProfileImport = serviceProvider.GetRequiredService<ILegacyProfileImportService>();
    private readonly IModDependencyService dependencies = serviceProvider.GetRequiredService<IModDependencyService>();
    private readonly IModMetadataService metadataService = serviceProvider.GetRequiredService<IModMetadataService>();
    private readonly IModConflictService conflictService = serviceProvider.GetRequiredService<IModConflictService>();
    private readonly IConfigurationDocumentCatalog configurationCatalog = serviceProvider.GetRequiredService<IConfigurationDocumentCatalog>();
    private readonly IWorkshopOperationCoordinator workshopOperations = serviceProvider.GetRequiredService<IWorkshopOperationCoordinator>();
    private readonly IWorkshopSubscriptionCoordinator subscriptions = serviceProvider.GetRequiredService<IWorkshopSubscriptionCoordinator>();
    private readonly IModRemovalFilesystem removalFilesystem = serviceProvider.GetRequiredService<IModRemovalFilesystem>();
    private readonly IModDuplicateAnalyzer duplicateAnalyzer = serviceProvider.GetRequiredService<IModDuplicateAnalyzer>();
    private readonly IDuplicatePreferenceService duplicatePreferences = serviceProvider.GetRequiredService<IDuplicatePreferenceService>();
    private readonly IWorkshopService workshopService = serviceProvider.GetRequiredService<IWorkshopService>();
    private readonly IWorkshopPreviewCache workshopPreviewCache = serviceProvider.GetRequiredService<IWorkshopPreviewCache>();
    private readonly IUpdateCheckService updateChecks = serviceProvider.GetRequiredService<IUpdateCheckService>();
    private readonly IApplicationDiagnostics diagnostics = serviceProvider.GetRequiredService<IApplicationDiagnostics>();
    private readonly IExistingModRootPreviewGuard modRootPreviewGuard = serviceProvider.GetRequiredService<IExistingModRootPreviewGuard>();
    private readonly IUiDispatcher uiDispatcher = serviceProvider.GetRequiredService<IUiDispatcher>();

    private const string ModsAutoSaveOwner = "mods";
    private const string SessionNotInitializedCode = "session.not_initialized";
    private const string SessionNotInitializedMessage = "AAML is not initialized.";
    private readonly SemaphoreSlim initialization = new(1, 1);
    private readonly SemaphoreSlim workshopGate = new(1, 1);
    private readonly SemaphoreSlim modRefreshGate = new(1, 1);
    private readonly AutoSaveCoordinator autoSave = new();
    private long modDraftRevision;
    private long modGridRevision;
    private bool disposed;
    private ApplicationSettings? settings;
    private SettingsOrigin? origin;
    private string status = "Not initialized";
    private bool initialized;
    private IReadOnlyList<ModInstallation> discoveredMods = [];
    private readonly Dictionary<string, IReadOnlyList<ModInstallation>> modDiscoveryCache = [];
    private readonly Dictionary<ModKey, ModIntentEdit> modDrafts = [];
    private readonly Dictionary<ModKey, DependencyStatus> dependencyStatuses = [];
    private readonly HashSet<ModKey> conflictingMods = [];
    private readonly Dictionary<ModKey, WorkshopModState> workshopStates = [];
    private readonly Dictionary<ModKey, string> workshopErrors = [];
    private readonly Dictionary<WorkshopId, string> retainedWorkshopStatuses = [];
    private WorkshopAvailability workshopAvailability = WorkshopAvailability.Unknown;
    private SessionUpdateCheck? updateCheck;
    private long updateCheckRevision;
    private long workshopProgressRevision;
    private ModDuplicateReport duplicateReport = new([], new Dictionary<ModKey, DuplicateStatus>());
    private IReadOnlySet<ModKey> focusedModKeys = new HashSet<ModKey>();
    private string modSearchText = string.Empty;
    private bool groupModsByCategory;
    private bool includeHidden = true;
    private ModGridSemanticState? modStateFilter;
    private readonly HashSet<ModGridGroupKey> collapsedModGroups = [];
    private ModProjectionStore? modProjectionStore;
    private static IReadOnlySet<TagId> EmptyTagIds { get; } = new HashSet<TagId>();

    private static Error NotInitializedError() => new(SessionNotInitializedCode, SessionNotInitializedMessage, ErrorKind.Unavailable);
    private static Result NotInitializedFailure() => Result.Failure(NotInitializedError());
    private static Result<T> NotInitializedFailure<T>() => Result<T>.Failure(NotInitializedError());
    private static Result<GameLaunchOutcome> NotInitializedGameLaunchFailure() => Result<GameLaunchOutcome>.Failure(NotInitializedError());

    public void RegisterAutoSaveOwner(string owner, Func<bool> isDirty, Func<CancellationToken, Task<Result>> save) => autoSave.Register(owner, isDirty, save);
    public void ActivateAutoSaveOwner(string owner) => autoSave.Activate(owner);
    public void NotifyAutoSaveOwnerChanged(string owner, bool immediate = false) => autoSave.Changed(owner, immediate);
    public Task<Result> FlushAutoSaveOwnerAsync(string owner, CancellationToken cancellationToken) => autoSave.FlushAsync(owner, cancellationToken);
    public void CancelAutoSaveOwner(string owner) => autoSave.Cancel(owner);
    public Task CancelAutoSaveOwnerAndWaitAsync(string owner, CancellationToken cancellationToken) => autoSave.CancelAndWaitAsync(owner, cancellationToken);

    public ApplicationSettings? Settings
    {
        get => settings;
        private set => uiDispatcher.Invoke(() =>
        {
            if (ReferenceEquals(settings, value)) return;
            modRootPreviewGuard.Clear();
            this.RaiseAndSetIfChanged(ref settings, value);
        });
    }
    public SettingsOrigin? Origin { get => origin; private set => uiDispatcher.Invoke(() => this.RaiseAndSetIfChanged(ref origin, value)); }
    public string Status { get => status; private set => uiDispatcher.Invoke(() => this.RaiseAndSetIfChanged(ref status, value)); }
    public ObservableCollection<SessionModRow> ModRows { get; } = [];
    public ObservableCollection<SessionProfile> Profiles { get; } = [];
    public ObservableCollection<SessionConflict> Conflicts { get; } = [];
    public ObservableCollection<ConfigurationDocumentSummary> ConfigurationDocuments { get; } = [];
    public bool HasFocusedMods => focusedModKeys.Count > 0;
    public int UnsavedModDraftCount => Settings is null ? 0 : modDrafts.Values.Count(IsUnsavedDraft);
    public bool HasUnsavedModDrafts => UnsavedModDraftCount > 0;
    public WorkshopAvailability WorkshopAvailability { get => workshopAvailability; private set => uiDispatcher.Invoke(() => this.RaiseAndSetIfChanged(ref workshopAvailability, value)); }
    public bool IsWorkshopBusy => workshopGate.CurrentCount == 0;
    public IReadOnlyList<Category> Categories => Settings?.Categories ?? [];
    public IReadOnlyList<Tag> Tags => Settings?.Tags ?? [];
    public ReleaseInfo? LatestRelease { get; private set; }
    public IReadOnlyList<ModInstallation> DiscoveredMods => discoveredMods;
    public SessionUpdateCheck? UpdateCheck { get => updateCheck; private set => uiDispatcher.Invoke(() => this.RaiseAndSetIfChanged(ref updateCheck, value)); }

    internal void PrimeSettings(ApplicationSettings initialSettings)
    {
        ArgumentNullException.ThrowIfNull(initialSettings);
        if (Settings is not null) return;
        Settings = initialSettings;
        autoSave.SetEnabled(initialSettings.AutoSaveChanges);
        Origin = SettingsOrigin.Existing;
    }

    public async Task<Result> AcceptMigratedSettingsAsync(ApplicationSettings migrated, CancellationToken cancellationToken)
    {
        Settings = migrated;
        Status = "Migration applied";
        return await RefreshModsAndConfigurationsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        await initialization.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return Result.Success();
            diagnostics.Write(LocalLogLevel.Information, "application.initialization_started", "Application initialization started.");
            Status = "Loading settings";
            if (Settings is null)
            {
                var result = await bootstrapper.InitializeAsync(cancellationToken);
                if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
                Settings = result.Value!.Settings;
                Origin = result.Value.Origin;
            }
            var grid = Settings.ModGrid ?? ModGridPreferences.Default;
            includeHidden = grid.IncludeHidden;
            modStateFilter = grid.StateFilter;
            groupModsByCategory = grid.GroupByCategory;
            collapsedModGroups.Clear(); foreach (var key in grid.CollapsedGroups) collapsedModGroups.Add(key);
            autoSave.SetEnabled(Settings.AutoSaveChanges);
            autoSave.Register(ModsAutoSaveOwner, () => HasUnsavedModDrafts || modGridRevision != 0, SaveModsOwnedDraftsAsync,
                token => uiDispatcher.InvokeAsync(() => HasUnsavedModDrafts || modGridRevision != 0, token));
            Status = $"Settings {Origin}";
            if (string.IsNullOrWhiteSpace(Settings.GameInstallationLocation))
            {
                var detected = await steamSettings.DiscoverAndApplyAsync(Settings, cancellationToken);
                if (detected.IsSuccess)
                {
                    Settings = detected.Value!.Settings with { NavigationRailMode = Settings.NavigationRailMode };
                    Status = "Detected Steam installation";
                }
                else Status = $"Steam detection: {detected.Error!.Message}";
            }
            var refreshed = await RefreshModsAsync(cancellationToken);
            if (!refreshed.IsSuccess) return refreshed;
            var configurations = await RefreshConfigurationDocumentsAsync(cancellationToken);
            if (!configurations.IsSuccess) return configurations;
            var profiles = await RefreshProfilesAsync(cancellationToken);
            if (!profiles.IsSuccess) return profiles;
            await ApplyStartupWorkshopPolicyAsync(cancellationToken);
            diagnostics.Write(LocalLogLevel.Information, "application.initialization_completed", "Application initialization completed.", new Dictionary<string, string> { ["game"] = Settings.SelectedGame.ToString(), ["origin"] = Origin.ToString()! });
            if (Settings.CheckForUpdates) await CheckForUpdatesAsync(false, cancellationToken).ConfigureAwait(false);
            await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
            initialized = true;
            return Result.Success();
        }
        finally { initialization.Release(); }
    }

    public async Task<Result<UpdateCheckResult>> CheckForUpdatesAsync(bool manual, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure<UpdateCheckResult>();
        var revision = Interlocked.Increment(ref updateCheckRevision);
        var channel = Settings.UpdateChannel;
        diagnostics.Write(LocalLogLevel.Information, "update.check_started", manual ? "Manual update check started." : "Startup update check started.");
        var result = await updateChecks.CheckAsync(channel, cancellationToken).ConfigureAwait(false);
        if (revision != Volatile.Read(ref updateCheckRevision)) return result;
        if (!result.IsSuccess)
        {
            UpdateCheck = new SessionUpdateCheck(manual, DateTimeOffset.UtcNow, channel, null, result.Error);
            LatestRelease = null;
            this.RaisePropertyChanged(nameof(LatestRelease));
            diagnostics.Write(LocalLogLevel.Warning, "update.check_failed", result.Error!.Message, new Dictionary<string, string> { ["code"] = result.Error.Code });
            if (manual) Status = result.Error.Message;
            await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        UpdateCheck = new SessionUpdateCheck(manual, DateTimeOffset.UtcNow, channel, result.Value, null);
        LatestRelease = result.Value!.Status == UpdateCheckStatus.UpdateAvailable ? result.Value.Release : null;
        if (manual || LatestRelease is not null) Status = result.Value.Message;
        this.RaisePropertyChanged(nameof(LatestRelease));
        await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<Result> SelectGameAsync(GameVariant game, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var result = await bootstrapper.SelectGameAsync(Settings, game, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value;
        Status = $"Selected {game}";
        return await RefreshModsAndConfigurationsAsync(cancellationToken, forceRefresh: false);
    }

    public async Task<Result> SetGameInstallationAsync(string installationPath, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var result = await bootstrapper.SetGameInstallationAsync(Settings, installationPath, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value;
        Status = "Game installation saved";
        return Result.Success();
    }

    public async Task<Result> SavePreferencesAsync(PreferenceSaveRequest request, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var result = await autoSave.SerializeAsync(token => bootstrapper.SavePreferencesAsync(
            Settings,
            request.Arguments,
            request.Roots,
            request.AllowMissingDependencies,
            request.CloseAfterLaunch,
            request.StartupRefresh,
            request.Theme,
            request.AllowMultipleInstances,
            request.CheckForUpdates,
            request.UpdateChannel,
            request.TextScale,
            request.IconScale,
            token), cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value;
        Status = "Preferences saved";
        return Result.Success();
    }

    public async Task<Result> ReloadSettingsAsync(CancellationToken cancellationToken)
    {
        var result = await bootstrapper.InitializeAsync(cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value!.Settings;
        Origin = result.Value.Origin;
        ResetDraftsFromSettings();
        return await RefreshModsAndConfigurationsAsync(cancellationToken);
    }

    public async Task<Result<ApplicationSettings>> ReadPersistedSettingsAsync(CancellationToken cancellationToken)
    {
        var result = await bootstrapper.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result<ApplicationSettings>.Failure(result.Error); }
        return Result<ApplicationSettings>.Success(result.Value!.Settings);
    }

    public async Task DiscardModsOwnedDraftsAsync(CancellationToken cancellationToken)
    {
        await autoSave.CancelAndWaitAsync(ModsAutoSaveOwner, cancellationToken);
        modDraftRevision++;
        ResetDraftsFromSettings();
        var grid = Settings?.ModGrid ?? ModGridPreferences.Default;
        includeHidden = grid.IncludeHidden;
        modStateFilter = grid.StateFilter;
        groupModsByCategory = grid.GroupByCategory;
        collapsedModGroups.Clear();
        foreach (var key in grid.CollapsedGroups) collapsedModGroups.Add(key);
        modGridRevision = 0;
        ProjectMods(modSearchText, groupModsByCategory);
        Status = "Discarded unsaved mod and view edits";
    }

    public async Task<Result> DetectSteamAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        Status = "Detecting Steam installation";
        var result = await steamSettings.DiscoverAndApplyAsync(Settings, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value!.Settings;
        Status = $"Detected {Settings.GameInstallationLocation}";
        return await RefreshModsAndConfigurationsAsync(cancellationToken);
    }

    public async Task<Result> RefreshModsAsync(CancellationToken cancellationToken, bool forceRefresh = true)
    {
        await modRefreshGate.WaitAsync(cancellationToken);
        try
        {
            if (Settings is null) return NotInitializedFailure();
            var cacheKey = ComputeModDiscoveryKey(Settings.ModRootLocations);
            IReadOnlyList<ModInstallation>? cached = null;
            if (!forceRefresh)
            {
                modDiscoveryCache.TryGetValue(cacheKey, out cached);
            }
            IReadOnlyList<ModInstallation> snapshot;
            if (cached is not null)
            {
                snapshot = cached;
            }
            else
            {
                Status = "Discovering mods";
                var result = await catalog.DiscoverAsync(Settings.ModRootLocations, null, cancellationToken);
                if (!result.IsSuccess) { Status = result.Error!.Message; diagnostics.Write(LocalLogLevel.Warning, "mods.discovery_failed", result.Error.Message, new Dictionary<string, string> { ["code"] = result.Error.Code }); return Result.Failure(result.Error); }
                snapshot = result.Value!;
                modDiscoveryCache[cacheKey] = snapshot;
            }
            var usedCache = cached is not null;
            var applied = await ApplyDiscoveredModsAsync(snapshot, cancellationToken);
            if (!applied.IsSuccess) return applied;
            Status = $"Discovered {discoveredMods.Count:N0} mods";
            diagnostics.Write(LocalLogLevel.Information, "mods.discovery_completed", "Mod discovery completed.", new Dictionary<string, string> { ["count"] = discoveredMods.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["cached"] = usedCache.ToString() });
            return Result.Success();
        }
        finally { modRefreshGate.Release(); }
    }

    private static string ComputeModDiscoveryKey(IReadOnlyList<string> roots) =>
        string.Join("|", roots.Where(root => !string.IsNullOrWhiteSpace(root)).OrderBy(root => root, StringComparer.OrdinalIgnoreCase));

    private async Task<Result> ApplyDiscoveredModsAsync(IReadOnlyList<ModInstallation> snapshot, CancellationToken cancellationToken)
    {
        await uiDispatcher.InvokeAsync(() => ApplyDiscoverySnapshot(snapshot), cancellationToken);
        await RefreshDependencyStatusesAsync(cancellationToken);
        var conflictInput = await uiDispatcher.InvokeAsync(() => new ConflictAnalysisInput(discoveredMods.ToArray(), EffectiveActiveModKeys()), cancellationToken);
        var conflictResult = await conflictService.AnalyzeAsync(conflictInput.DiscoveredMods, conflictInput.ActiveMods, cancellationToken);
        if (!conflictResult.IsSuccess) { Status = conflictResult.Error!.Message; return Result.Failure(conflictResult.Error); }
        ApplyConflicts(conflictResult.Value!);
        ProjectMods(modSearchText, groupModsByCategory);
        return Result.Success();
    }

    private void ApplyDiscoverySnapshot(IReadOnlyList<ModInstallation> snapshot)
    {
        discoveredMods = snapshot;
        this.RaisePropertyChanged(nameof(DiscoveredMods));
        var discoveredKeys = discoveredMods.Select(mod => mod.Key).ToHashSet();
        RemoveUnknownEntries(workshopStates, discoveredKeys);
        RemoveUnknownEntries(workshopErrors, discoveredKeys);
        RemoveUnknownEntries(modDrafts, discoveredKeys);
        duplicateReport = duplicateAnalyzer.Analyze(discoveredMods, Settings!.DuplicatePreferences ?? []);
        var intents = Settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var modKey in discoveredMods.Select(mod => mod.Key).Where(modKey => !modDrafts.ContainsKey(modKey)))
        {
            intents.TryGetValue(modKey, out var intent);
            modDrafts[modKey] = new ModIntentEdit(modKey, intent?.IsActive ?? false, intent?.ExplicitOrder);
        }
    }

    private static void RemoveUnknownEntries<TValue>(IDictionary<ModKey, TValue> items, IReadOnlySet<ModKey> validKeys)
    {
        foreach (var key in items.Keys.Where(item => !validKeys.Contains(item)).ToArray())
        {
            items.Remove(key);
        }
    }

    public async Task<Result> RefreshModsAndConfigurationsAsync(CancellationToken cancellationToken, bool forceRefresh = true)
    {
        var mods = await RefreshModsAsync(cancellationToken, forceRefresh).ConfigureAwait(false);
        return mods.IsSuccess ? await RefreshConfigurationDocumentsAsync(cancellationToken).ConfigureAwait(false) : mods;
    }

    public async Task<Result> RefreshConflictsAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        Status = "Analyzing conflicts";
        var result = await conflictService.AnalyzeAsync(discoveredMods, EffectiveActiveModKeys(), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        ApplyConflicts(result.Value!);
        ProjectMods(modSearchText, groupModsByCategory);
        Status = $"Analyzed {discoveredMods.Count:N0} mods for conflicts";
        return Result.Success();
    }

    public void ProjectMods(string searchText, bool groupByCategory)
    {
        uiDispatcher.Invoke(() => ProjectModsCore(searchText, groupByCategory));
    }

    private void ProjectModsCore(string searchText, bool groupByCategory)
    {
        if (Settings is null) return;
        modSearchText = searchText ?? string.Empty;
        groupModsByCategory = groupByCategory;
        var intents = Settings.ModIntents.ToDictionary(intent => intent.Mod);
        var entries = discoveredMods.Select(mod => CreateProjectionEntry(mod, intents.GetValueOrDefault(mod.Key))).ToArray();
        var query = ModGridQuery.Default with
        {
            SearchText = modSearchText,
            IncludeHidden = includeHidden,
            StateFilters = modStateFilter is { } filter ? new HashSet<ModGridSemanticState> { filter } : new HashSet<ModGridSemanticState>(),
            Grouping = groupModsByCategory ? ModGridGrouping.Category : ModGridGrouping.None,
            Sort = new ModGridSort(ModGridSortColumn.Order, SortDirection.Ascending)
        };
        modProjectionStore ??= new(ModRows, UpdateDraft);
        modProjectionStore.Apply(entries, new(query, new(Settings.Categories, Settings.Tags), collapsedModGroups.ToHashSet(), focusedModKeys.ToHashSet(),
            Settings.RetainedWorkshopItems ?? [], new Dictionary<WorkshopId, string>(retainedWorkshopStatuses)));
    }

    private ModProjectionEntry CreateProjectionEntry(ModInstallation mod) => CreateProjectionEntry(mod, Settings!.ModIntents.FirstOrDefault(item => item.Mod == mod.Key));

    private ModProjectionEntry CreateProjectionEntry(ModInstallation mod, ModUserIntent? intent)
    {
        var draft = modDrafts.GetValueOrDefault(mod.Key) ?? new ModIntentEdit(mod.Key, intent?.IsActive ?? false, intent?.ExplicitOrder);
        var item = new ModGridItem(
            mod.Key, mod.PackageId, mod.WorkshopId, intent?.ManualName ?? mod.Name,
            draft.IsActive, intent?.IsHidden ?? false, draft.ExplicitOrder,
            intent?.Category, intent?.Tags ?? EmptyTagIds,
            new ModStatus(InstallationStatus.Installed, duplicateReport.Status(mod.Key), dependencyStatuses.GetValueOrDefault(mod.Key, DependencyStatus.Unknown), conflictingMods.Contains(mod.Key) ? ConflictStatus.Conflicting : ConflictStatus.None, workshopStates.GetValueOrDefault(mod.Key)?.Update ?? UpdateStatus.Unknown),
            mod.RequiresWarOfTheChosen, mod.DateAdded);
        return new(mod, item, workshopStates.GetValueOrDefault(mod.Key), workshopErrors.GetValueOrDefault(mod.Key));
    }

    private void RefreshProjectionKeys(IEnumerable<ModKey> keys)
    {
        uiDispatcher.Invoke(() => RefreshProjectionKeysCore(keys));
    }

    private void RefreshProjectionKeysCore(IEnumerable<ModKey> keys)
    {
        if (modProjectionStore is null) { ProjectModsCore(modSearchText, groupModsByCategory); return; }
        var keySet = keys.ToHashSet();
        modProjectionStore.UpdateEntries(discoveredMods.Where(mod => keySet.Contains(mod.Key)).Select(CreateProjectionEntry).ToArray());
    }

    public void SetModGrouping(bool groupByCategory)
    {
        if (this.groupModsByCategory == groupByCategory) return;
        this.groupModsByCategory = groupByCategory;
        modGridRevision++;
        ProjectMods(modSearchText, groupByCategory);
        autoSave.Changed(ModsAutoSaveOwner);
    }

    public void SetModGridFilter(bool showHidden, ModGridSemanticState? state)
    {
        includeHidden = showHidden;
        modStateFilter = state;
        modGridRevision++;
        ProjectMods(modSearchText, groupModsByCategory);
        autoSave.Changed(ModsAutoSaveOwner);
    }

    public void ToggleModGroup(ModGridGroupKey key)
    {
        if (!collapsedModGroups.Add(key)) collapsedModGroups.Remove(key);
        modGridRevision++;
        ProjectMods(modSearchText, groupModsByCategory);
        autoSave.Changed(ModsAutoSaveOwner);
    }

    public async Task<Result> SaveModGridPreferencesAsync(CancellationToken cancellationToken)
    {
        return await autoSave.SerializeAsync(async token =>
        {
            var capture = await uiDispatcher.InvokeAsync(() => Settings is null
                ? null
                : new ModGridSaveCapture(Settings, modGridRevision, new ModGridPreferences(includeHidden, modStateFilter, groupModsByCategory, collapsedModGroups.ToHashSet())), token).ConfigureAwait(false);
            if (capture is null) return NotInitializedFailure();
            var result = await bootstrapper.SaveModGridPreferencesAsync(capture.Settings, capture.Preferences, token).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                var error = result.Error!;
                await uiDispatcher.InvokeAsync(() => Status = error.Message, token).ConfigureAwait(false);
                return Result.Failure(error);
            }
            await uiDispatcher.InvokeAsync(() =>
            {
                Settings = result.Value;
                if (capture.Revision == modGridRevision) modGridRevision = 0;
                Status = "Mod view saved";
            }, token).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> SetNavigationRailModeAsync(NavigationRailMode mode, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var result = await autoSave.SerializeAsync(token => bootstrapper.SetNavigationRailModeAsync(Settings, mode, token), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value;
        return Result.Success();
    }

    public async Task<Result> SetAutoSaveChangesAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var preference = await autoSave.SerializeAsync(token => bootstrapper.SetAutoSaveChangesAsync(Settings, enabled, token), cancellationToken).ConfigureAwait(false);
        if (!preference.IsSuccess) { Status = preference.Error!.Message; return Result.Failure(preference.Error); }
        Settings = preference.Value;
        autoSave.SetEnabled(enabled);
        Status = enabled ? "Auto-save enabled" : "Auto-save disabled";
        return enabled ? await autoSave.FlushActiveAsync(cancellationToken).ConfigureAwait(false) : Result.Success();
    }

    public Result<int> SetSelectedActive(IReadOnlySet<ModKey> keys, bool active)
    {
        if (keys.Count == 0) return BulkActivationFailure("mods.selection_empty", "Select at least one mod.");
        var changed = 0;
        var missing = new List<ModKey>();
        var duplicates = new List<ModKey>();
        foreach (var key in keys)
        {
            if (!modDrafts.TryGetValue(key, out var edit)) { missing.Add(key); continue; }
            if (active && duplicateReport.Status(key) is not (DuplicateStatus.None or DuplicateStatus.Preferred)) { duplicates.Add(key); continue; }
            if (edit.IsActive == active) continue;
            modDrafts[key] = edit with { IsActive = active };
            changed++;
        }
        ProjectMods(modSearchText, groupModsByCategory);
        modDraftRevision++;
        RaiseDraftStateChanged();
        var skipped = missing.Count + duplicates.Count;
        if (changed == 0)
        {
            var reason = BuildBulkActivationReason(active, skipped, missing, duplicates);
            return BulkActivationFailure("mods.activation_no_changes", reason);
        }

        var summaryStatus = BuildBulkActivationStatus(active, changed, skipped, missing, duplicates);
        Status = summaryStatus;
        return Result<int>.Success(changed);
    }

    private static string BuildBulkActivationReason(bool active, int skipped, IReadOnlyCollection<ModKey> missing, IReadOnlyCollection<ModKey> duplicates)
    {
        if (skipped > 0) return BulkSkipMessage(missing, duplicates);
        return active ? "All selected mods are already active." : "All selected mods are already inactive.";
    }

    private static string BuildBulkActivationStatus(bool active, int changed, int skipped, IReadOnlyCollection<ModKey> missing, IReadOnlyCollection<ModKey> duplicates)
    {
        var action = active ? "Activated" : "Deactivated";
        var pluralSuffix = GetPluralSuffix(changed);
        var summary = $"{action} {changed:N0} selected mod{pluralSuffix}";
        if (skipped == 0) return summary;
        return $"{summary}; {BulkSkipMessage(missing, duplicates)}";
    }

    private static string GetPluralSuffix(int count) => count == 1 ? string.Empty : "s";

    private static string BuildSavedDraftStatus(bool isLatestRevision, int activeModCount, int unsavedDraftCount)
    {
        if (isLatestRevision)
        {
            return $"Saved {activeModCount:N0} active mods";
        }

        var pluralSuffix = unsavedDraftCount == 1 ? string.Empty : "s";
        return $"Saved an earlier mod snapshot; {unsavedDraftCount:N0} newer edit{pluralSuffix} remain unsaved";
    }

    public async Task<Result<int>> SetSelectedActiveAndSaveAsync(IReadOnlySet<ModKey> keys, bool active, CancellationToken cancellationToken)
    {
        var changed = SetSelectedActive(keys, active);
        if (!changed.IsSuccess || Settings?.AutoSaveChanges != true) return changed;
        var saved = await SaveModDraftsAsync(cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? changed : Result<int>.Failure(saved.Error!);
    }

    public void MoveSelected(IReadOnlySet<ModKey> keys, int delta)
    {
        var before = modDrafts.Values.ToDictionary(edit => edit.Mod);
        var ordered = discoveredMods.Select(mod => mod.Key).OrderBy(key => modDrafts.GetValueOrDefault(key)?.ExplicitOrder ?? int.MaxValue).ThenBy(key => key.LocationIdentity, StringComparer.Ordinal).ToList();
        var selected = ordered.Where(keys.Contains).ToArray();
        foreach (var key in delta < 0 ? selected : selected.Reverse())
        {
            var index = ordered.IndexOf(key); var target = Math.Clamp(index + delta, 0, ordered.Count - 1);
            ordered.RemoveAt(index); ordered.Insert(target, key);
        }
        for (var index = 0; index < ordered.Count; index++) if (modDrafts.TryGetValue(ordered[index], out var edit)) modDrafts[ordered[index]] = edit with { ExplicitOrder = index };
        if (modDrafts.Values.All(edit => before.GetValueOrDefault(edit.Mod) == edit)) return;
        ProjectMods(modSearchText, groupModsByCategory);
        modDraftRevision++;
        RaiseDraftStateChanged();
    }

    public void RenumberMods() => MoveSelected(new HashSet<ModKey>(), 0);

    public async Task<Result> MoveSelectedAndSaveAsync(IReadOnlySet<ModKey> keys, int delta, CancellationToken cancellationToken)
    {
        var revision = modDraftRevision;
        MoveSelected(keys, delta);
        if (revision == modDraftRevision || Settings?.AutoSaveChanges != true) return Result.Success();
        return await SaveModDraftsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> RenumberModsAndSaveAsync(CancellationToken cancellationToken)
    {
        var revision = modDraftRevision;
        RenumberMods();
        if (revision == modDraftRevision || Settings?.AutoSaveChanges != true) return Result.Success();
        return await SaveModDraftsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> SaveModDraftsAsync(CancellationToken cancellationToken)
    {
        autoSave.Cancel(ModsAutoSaveOwner);
        return await autoSave.SerializeAsync(SaveModDraftSnapshotAsync, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> SaveModsOwnedDraftsAsync(CancellationToken cancellationToken)
    {
        if (await uiDispatcher.InvokeAsync(() => HasUnsavedModDrafts, cancellationToken).ConfigureAwait(false))
        {
            var mods = await autoSave.SerializeAsync(SaveModDraftSnapshotAsync, cancellationToken).ConfigureAwait(false);
            if (!mods.IsSuccess) return mods;
        }
        var gridDirty = await uiDispatcher.InvokeAsync(() => modGridRevision != 0, cancellationToken).ConfigureAwait(false);
        return gridDirty ? await SaveModGridPreferencesAsync(cancellationToken).ConfigureAwait(false) : Result.Success();
    }

    private async Task<Result> SaveModDraftSnapshotAsync(CancellationToken cancellationToken)
    {
        var capture = await uiDispatcher.InvokeAsync(() => Settings is null
            ? null
            : new ModDraftSaveCapture(Settings, modDraftRevision, modDrafts.Values.ToArray(), discoveredMods.ToArray(), duplicateReport), cancellationToken).ConfigureAwait(false);
        if (capture is null) return NotInitializedFailure();
        await uiDispatcher.InvokeAsync(() => Status = "Saving mod activation and load order", cancellationToken).ConfigureAwait(false);
        var validation = ModDuplicateActivationPolicy.Validate(capture.DiscoveredMods, capture.Drafts, capture.DuplicateReport);
        if (!validation.IsSuccess)
        {
            await uiDispatcher.InvokeAsync(() => Status = validation.Error!.Message, cancellationToken).ConfigureAwait(false);
            return validation;
        }
        var result = await modIntents.SaveAsync(capture.Settings, capture.Drafts, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var error = result.Error!;
            await uiDispatcher.InvokeAsync(() => Status = error.Message, cancellationToken).ConfigureAwait(false);
            return Result.Failure(error);
        }
        var updated = result.Value!;
        await uiDispatcher.InvokeAsync(() =>
        {
            Settings = updated;
            RaiseDraftStateChanged();
        }, cancellationToken).ConfigureAwait(false);
        await RefreshDependencyStatusesAsync(cancellationToken);
        var activeKeys = await uiDispatcher.InvokeAsync(EffectiveActiveModKeys, cancellationToken).ConfigureAwait(false);
        var conflictResult = await conflictService.SetActiveAsync(activeKeys, cancellationToken).ConfigureAwait(false);
        if (!conflictResult.IsSuccess)
        {
            var error = conflictResult.Error!;
            await uiDispatcher.InvokeAsync(() => Status = error.Message, cancellationToken).ConfigureAwait(false);
            return Result.Failure(error);
        }
        await uiDispatcher.InvokeAsync(() =>
        {
            ApplyConflictsCore(conflictResult.Value!);
            var summary = BuildSavedDraftStatus(capture.Revision == modDraftRevision, updated.ModIntents.Count(intent => intent.IsActive), UnsavedModDraftCount);
            Status = summary;
            ProjectModsCore(modSearchText, groupModsByCategory);
        }, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> PreferDuplicateAsync(ModKey mod, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var result = await duplicatePreferences.PreferAsync(Settings, discoveredMods, mod, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        var updated = result.Value!;
        Settings = updated;
        ResetDraftsFromSettings();
        duplicateReport = duplicateAnalyzer.Analyze(discoveredMods, updated.DuplicatePreferences ?? []);
        ProjectMods(modSearchText, groupModsByCategory);
        Status = "Preferred duplicate installation saved";
        return Result.Success();
    }

    public async Task<Result> ClearDuplicatePreferenceAsync(ModKey mod, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var installation = discoveredMods.SingleOrDefault(item => item.Key == mod);
        if (installation is null) return Result.Failure(new Error("duplicates.mod_missing", "The selected installation is no longer discovered.", ErrorKind.NotFound));
        var result = await duplicatePreferences.ClearAsync(Settings, discoveredMods, installation.PackageId, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        var updated = result.Value!;
        Settings = updated;
        ResetDraftsFromSettings();
        duplicateReport = duplicateAnalyzer.Analyze(discoveredMods, updated.DuplicatePreferences ?? []);
        ProjectMods(modSearchText, groupModsByCategory);
        Status = "Duplicate preference cleared and group deactivated";
        return Result.Success();
    }

    public async Task<Result> RefreshProfilesAsync(CancellationToken cancellationToken)
    {
        var result = await profileService.ListAsync(cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Profiles.Clear();
        foreach (var profile in result.Value!) Profiles.Add(new SessionProfile(profile.Id, profile.Name, profile.GameVariant, profile.Mods.Count));
        Status = $"Loaded {Profiles.Count:N0} profiles";
        return Result.Success();
    }

    public async Task<Result> CreateProfileAsync(string name, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var effective = modIntents.Merge(Settings, modDrafts.Values.ToArray());
        if (!effective.IsSuccess) { Status = effective.Error!.Message; return Result.Failure(effective.Error); }
        var result = await profileService.CreateAsync(name, effective.Value!, discoveredMods, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        await RefreshProfilesAsync(cancellationToken);
        Status = $"Created profile {result.Value!.Name}";
        return Result.Success();
    }

    public async Task<Result> ApplyProfileAsync(ProfileId id, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var result = await profileService.ApplyAsync(id, Settings, discoveredMods, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        if (!result.Value!.Applied)
        {
            Status = string.Join("; ", result.Value.Diagnostics.Select(diagnostic => diagnostic.Message));
            return Result.Failure(new Error("profile.apply_blocked", Status, ErrorKind.Conflict));
        }
        Settings = result.Value.Settings;
        ResetDraftsFromSettings();
        await RefreshDependencyStatusesAsync(cancellationToken);
        var conflictResult = await conflictService.SetActiveAsync(EffectiveActiveModKeys(), cancellationToken);
        if (!conflictResult.IsSuccess) { Status = conflictResult.Error!.Message; return Result.Failure(conflictResult.Error); }
        ApplyConflicts(conflictResult.Value!);
        ProjectMods(modSearchText, groupModsByCategory);
        Status = $"Applied profile {result.Value.Profile.Name}";
        return Result.Success();
    }

    public async Task<Result> DeleteProfileAsync(ProfileId id, CancellationToken cancellationToken)
    {
        var result = await profileService.DeleteAsync(id, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return result; }
        await RefreshProfilesAsync(cancellationToken);
        Status = "Profile deleted";
        return Result.Success();
    }

    public async Task<Result> RenameProfileAsync(ProfileId id, string name, CancellationToken cancellationToken)
    {
        var result = await profileService.RenameAsync(id, name, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        await RefreshProfilesAsync(cancellationToken);
        Status = $"Renamed profile to {result.Value!.Name}";
        return Result.Success();
    }

    public async Task<Result> DuplicateProfileAsync(ProfileId id, string name, CancellationToken cancellationToken)
    {
        var result = await profileService.DuplicateAsync(id, name, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        await RefreshProfilesAsync(cancellationToken);
        Status = $"Duplicated profile as {result.Value!.Name}";
        return Result.Success();
    }

    public Task<Result<string>> ExportProfileAsync(ProfileId id, CancellationToken cancellationToken) => profileInterchange.ExportAsync(id, cancellationToken);

    public async Task<Result> ImportProfileAsync(string document, CancellationToken cancellationToken)
    {
        var result = await profileInterchange.ImportAsync(document, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        await RefreshProfilesAsync(cancellationToken);
        Status = $"Imported profile {result.Value!.Name}";
        return Result.Success();
    }

    public Result<LegacyProfilePreview> PreviewLegacyProfile(string document)
    {
        var preview = legacyProfileImport.Preview(document);
        if (!preview.IsSuccess) return preview;
        var resolutions = preview.Value!.Entries.Select(entry =>
        {
            var matches = discoveredMods.Where(mod => entry.WorkshopId.HasValue ? mod.WorkshopId?.Value == entry.WorkshopId.Value : mod.PackageId.Value.Equals(entry.PackageId, StringComparison.OrdinalIgnoreCase)).ToArray();
            var resolution = matches.Length switch
            {
                1 => "installed",
                0 when entry.WorkshopId.HasValue => "missing Workshop item",
                0 => "missing local item",
                _ => "ambiguous duplicate"
            };
            return $"Line {entry.LineNumber}: resolution = {resolution}";
        });
        return Result<LegacyProfilePreview>.Success(preview.Value with { Report = preview.Value.Report + "\nResolution:\n" + string.Join('\n', resolutions) });
    }

    public async Task<Result> ImportLegacyProfileAsync(string name, LegacyProfilePreview preview, LegacyTaxonomyDisposition taxonomy, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var imported = await legacyProfileImport.ImportAsync(name, preview, taxonomy, Settings, discoveredMods, cancellationToken).ConfigureAwait(false);
        if (!imported.IsSuccess)
        {
            Status = imported.Error!.Message;
            return Result.Failure(imported.Error);
        }

        if (taxonomy == LegacyTaxonomyDisposition.AdoptIntoApplication)
        {
            var adopted = await AdoptLegacyTaxonomyAsync(preview, cancellationToken).ConfigureAwait(false);
            if (!adopted.IsSuccess)
            {
                return Result.Failure(adopted.Error!);
            }

            Settings = adopted.Value!;
        }

        return await FinishLegacyImportAsync(imported.Value!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> FinishLegacyImportAsync(LegacyProfileImportResult imported, CancellationToken cancellationToken)
    {
        await RefreshProfilesAsync(cancellationToken).ConfigureAwait(false);
        Status = imported.Imported ? $"Imported legacy profile '{imported.Profile.Name}'" : $"Legacy profile '{imported.Profile.Name}' was already imported";
        return Result.Success();
    }

    private async Task<Result<ApplicationSettings>> AdoptLegacyTaxonomyAsync(LegacyProfilePreview preview, CancellationToken cancellationToken)
    {
        var currentSettings = Settings!;
        foreach (var entry in preview.Entries)
        {
            var match = ResolveLegacyProfileMatch(entry);
            if (match is null) continue;
            var adopted = await metadataService.AdoptDescriptorTaxonomyAsync(currentSettings, match.Key, entry.Category, entry.Tags ?? [], cancellationToken).ConfigureAwait(false);
            if (!adopted.IsSuccess)
            {
                return Result<ApplicationSettings>.Failure(adopted.Error!);
            }

            currentSettings = adopted.Value!;
        }

        return Result<ApplicationSettings>.Success(currentSettings);
    }

    private ModInstallation? ResolveLegacyProfileMatch(LegacyProfileEntry entry)
    {
        var matches = discoveredMods.Where(mod => entry.WorkshopId.HasValue
            ? mod.WorkshopId?.Value == entry.WorkshopId.Value
            : mod.PackageId.Value.Equals(entry.PackageId, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public async Task<Result<IReadOnlyList<SessionMissingProfileItem>>> GetMissingProfileItemsAsync(ProfileId profileId, CancellationToken cancellationToken)
    {
        var profiles = await profileService.ListAsync(cancellationToken); if (!profiles.IsSuccess) return Result<IReadOnlyList<SessionMissingProfileItem>>.Failure(profiles.Error!);
        var profile = profiles.Value!.SingleOrDefault(item => item.Id == profileId); if (profile is null) return Result<IReadOnlyList<SessionMissingProfileItem>>.Failure(new Error("profile.not_found", "Profile was not found.", ErrorKind.NotFound));
        return Result<IReadOnlyList<SessionMissingProfileItem>>.Success(profile.Mods
            .Where(entry => entry.WorkshopId.HasValue && discoveredMods.All(mod => mod.WorkshopId != entry.WorkshopId))
            .Select(entry => entry.WorkshopId is { } workshopId ? new SessionMissingProfileItem(workshopId, entry.PackageId.Value) : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray());
    }

    public async Task<Result> SubscribeProfileItemsAsync(IReadOnlyCollection<WorkshopId> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return Result.Failure(new Error("workshop.selection_empty", "The profile has no missing Workshop items.", ErrorKind.Validation));
        var result = await subscriptions.SubscribeAsync(ids, cancellationToken); await RefreshModsAndConfigurationsAsync(CancellationToken.None);
        await RefreshProfilesAsync(CancellationToken.None);
        Status = result.IsSuccess ? $"Subscribed to {result.Items.Count:N0} profile items" : $"Profile subscriptions: {result.Items.Count(item => item.Outcome.IsSuccess):N0} succeeded, {result.Items.Count(item => !item.Outcome.IsSuccess):N0} failed";
        return result.IsSuccess ? Result.Success() : Result.Failure(new Error("workshop.subscription_partial", Status, ErrorKind.ExternalService));
    }

    public SessionModMetadata? GetMetadata(ModKey key)
    {
        if (Settings is null) return null;
        var installation = discoveredMods.FirstOrDefault(mod => mod.Key == key);
        if (installation is null) return null;
        var intent = Settings.ModIntents.FirstOrDefault(item => item.Mod == key);
        return new SessionModMetadata(key, intent?.ManualName, intent?.Note, intent?.IsHidden ?? false, intent?.Category, intent?.Tags ?? new HashSet<TagId>());
    }

    public async Task<Result> SaveMetadataAsync(SessionModMetadata metadata, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var result = await metadataService.SaveAsync(Settings, metadata.Key,
            new ModMetadata(metadata.ManualName, metadata.Note, metadata.IsHidden, metadata.Category, metadata.Tags), cancellationToken);
        return ApplyMetadataResult(result, "Mod metadata saved");
    }

    public async Task<Result> AssignCategoryAsync(IReadOnlyCollection<ModKey> mods, CategoryId? category, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        return ApplyMetadataResult(await metadataService.AssignCategoryAsync(Settings, mods, category, cancellationToken), $"Updated category for {mods.Count:N0} mods");
    }

    public async Task<Result> AddTagsAsync(IReadOnlyCollection<ModKey> mods, IReadOnlySet<TagId> tags, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        return ApplyMetadataResult(await metadataService.AddTagsAsync(Settings, mods, tags, cancellationToken), $"Updated tags for {mods.Count:N0} mods");
    }

    public async Task<Result> RemoveTagsAsync(IReadOnlyCollection<ModKey> mods, IReadOnlySet<TagId> tags, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        return ApplyMetadataResult(await metadataService.RemoveTagsAsync(Settings, mods, tags, cancellationToken), $"Removed tags from {mods.Count:N0} mods");
    }

    public async Task<Result> SetHiddenAsync(IReadOnlyCollection<ModKey> mods, bool hidden, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        return ApplyMetadataResult(await metadataService.SetHiddenAsync(Settings, mods, hidden, cancellationToken), hidden ? $"Hidden {mods.Count:N0} mods" : $"Unhidden {mods.Count:N0} mods");
    }

    public async Task<Result> SetTagColorAsync(TagId id, string? color, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        return ApplyMetadataResult(await metadataService.SetTagColorAsync(Settings, id, color, cancellationToken), "Tag color saved");
    }

    public async Task<Result> AdoptDescriptorTaxonomyAsync(ModKey mod, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var installation = discoveredMods.SingleOrDefault(item => item.Key == mod);
        if (installation is null) return Result.Failure(new Error("mods.not_found", "The selected mod is no longer discovered.", ErrorKind.NotFound));
        return ApplyMetadataResult(await metadataService.AdoptDescriptorTaxonomyAsync(Settings, mod, installation.Metadata?.DescriptorCategory, installation.Metadata?.DescriptorTags ?? [], cancellationToken), "Descriptor taxonomy adopted");
    }

    public async Task<Result> CreateCategoryAsync(string name, CancellationToken cancellationToken) => ApplyMetadataResult(await metadataService.CreateCategoryAsync(Settings!, name, cancellationToken), "Category created");
    public async Task<Result> RenameCategoryAsync(CategoryId id, string name, CancellationToken cancellationToken) => ApplyMetadataResult(await metadataService.RenameCategoryAsync(Settings!, id, name, cancellationToken), "Category renamed");
    public async Task<Result> ReorderCategoryAsync(CategoryId id, int order, CancellationToken cancellationToken) => ApplyMetadataResult(await metadataService.ReorderCategoryAsync(Settings!, id, order, cancellationToken), "Category reordered");
    public async Task<Result> DeleteCategoryAsync(CategoryId id, CancellationToken cancellationToken) => ApplyMetadataResult(await metadataService.DeleteCategoryAsync(Settings!, id, cancellationToken), "Category deleted");
    public async Task<Result> CreateTagAsync(string name, CancellationToken cancellationToken) => ApplyMetadataResult(await metadataService.CreateTagAsync(Settings!, name, cancellationToken), "Tag created");
    public async Task<Result> RenameTagAsync(TagId id, string name, CancellationToken cancellationToken) => ApplyMetadataResult(await metadataService.RenameTagAsync(Settings!, id, name, cancellationToken), "Tag renamed");
    public async Task<Result> DeleteTagAsync(TagId id, CancellationToken cancellationToken) => ApplyMetadataResult(await metadataService.DeleteTagAsync(Settings!, id, cancellationToken), "Tag deleted");

    public async Task<Result<GameLaunchOutcome>> LaunchAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedGameLaunchFailure();
        diagnostics.Write(LocalLogLevel.Information, "game.launch_requested", "Game launch requested.", new Dictionary<string, string> { ["game"] = Settings.SelectedGame.ToString() });

        var savedSettings = await SaveActiveDraftsAsync(cancellationToken).ConfigureAwait(false);
        if (!savedSettings.IsSuccess) return Result<GameLaunchOutcome>.Failure(savedSettings.Error!);

        var activeSettings = savedSettings.Value ?? throw new InvalidOperationException("Application settings were not produced after the launch preflight save.");
        if (string.IsNullOrWhiteSpace(activeSettings.GameInstallationLocation))
        {
            Status = "Configure the selected game installation before launching.";
            return Result<GameLaunchOutcome>.Failure(new Error("launch.installation_required", Status, ErrorKind.Validation));
        }

        var dependencyResult = await EvaluateLaunchDependenciesAsync(activeSettings, cancellationToken).ConfigureAwait(false);
        if (!dependencyResult.IsSuccess)
        {
            Status = dependencyResult.Error!.Message;
            diagnostics.Write(LocalLogLevel.Error, "game.launch_failed", Status, new Dictionary<string, string> { ["code"] = dependencyResult.Error.Code });
            await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
            return Result<GameLaunchOutcome>.Failure(dependencyResult.Error);
        }

        if (dependencyResult.Value!.HasBlockingIssues && !activeSettings.AllowLaunchWithMissingDependencies)
        {
            Status = DescribeLaunchDependencyBlock(dependencyResult.Value);
            diagnostics.Write(LocalLogLevel.Warning, "game.launch_blocked", Status, new Dictionary<string, string>
            {
                ["code"] = "launch.dependencies_blocked",
                ["blockingIssueCount"] = dependencyResult.Value.Issues.Count(issue => issue.Kind is ModDependencyIssueKind.Missing or ModDependencyIssueKind.Inactive or ModDependencyIssueKind.MetadataUnavailable).ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
            return Result<GameLaunchOutcome>.Failure(new Error("launch.dependencies_blocked", Status, ErrorKind.Conflict));
        }

        Status = $"Launching {activeSettings.SelectedGame}";
        var request = BuildGameLaunchRequest(activeSettings, discoveredMods.ToDictionary(mod => mod.Key));
        var result = await launchCoordinator.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        Status = BuildLaunchStatus(result, activeSettings);
        diagnostics.Write(result.IsSuccess ? LocalLogLevel.Information : LocalLogLevel.Error, result.IsSuccess ? "game.launch_completed" : "game.launch_failed", Status, result.IsSuccess ? null : new Dictionary<string, string> { ["code"] = result.Error!.Code });
        await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static string BuildLaunchStatus(Result<GameLaunchOutcome> result, ApplicationSettings activeSettings)
    {
        if (result.IsSuccess)
        {
            var activePackageCount = result.Value!.Configuration?.ActivePackageIds.Count ?? 0;
            var launchProcessId = result.Value.Launch.ProcessId;
            return $"Started {activeSettings.SelectedGame} with {activePackageCount:N0} mods (process {launchProcessId})";
        }

        return result.Error!.Message;
    }

    private async Task<Result<ApplicationSettings>> SaveActiveDraftsAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure<ApplicationSettings>();

        var rootSafety = modRootPreviewGuard.EnsureConfigurationSafe(Settings.SelectedGame);
        if (!rootSafety.IsSuccess)
        {
            Status = rootSafety.Error!.Message;
            return Result<ApplicationSettings>.Failure(rootSafety.Error);
        }

        var duplicateValidation = ModDuplicateActivationPolicy.Validate(discoveredMods, modDrafts.Values, duplicateAnalyzer.Analyze(discoveredMods, Settings.DuplicatePreferences ?? []));
        if (!duplicateValidation.IsSuccess)
        {
            Status = duplicateValidation.Error!.Message;
            return Result<ApplicationSettings>.Failure(duplicateValidation.Error);
        }

        var savedIntents = await modIntents.SaveAsync(Settings, modDrafts.Values.ToArray(), cancellationToken).ConfigureAwait(false);
        if (!savedIntents.IsSuccess)
        {
            Status = savedIntents.Error!.Message;
            return Result<ApplicationSettings>.Failure(savedIntents.Error);
        }

        var updatedSettings = savedIntents.Value!;
        Settings = updatedSettings;
        ResetDraftsFromSettings();
        return Result<ApplicationSettings>.Success(updatedSettings);
    }

    private async Task<Result<ModDependencyReport>> EvaluateLaunchDependenciesAsync(ApplicationSettings settings, CancellationToken cancellationToken)
    {
        var installations = discoveredMods.ToDictionary(mod => mod.Key);
        var activeMods = settings.ModIntents
            .Where(intent => intent.IsActive && installations.ContainsKey(intent.Mod))
            .OrderBy(intent => intent.ExplicitOrder ?? int.MaxValue)
            .ThenBy(intent => installations[intent.Mod].PackageId.Value, StringComparer.OrdinalIgnoreCase)
            .Select(intent => installations[intent.Mod])
            .Where(mod => mod.WorkshopId.HasValue)
            .Select(mod => mod.WorkshopId!.Value)
            .ToHashSet();

        var installedWorkshop = discoveredMods.Where(mod => mod.WorkshopId.HasValue).Select(mod => mod.WorkshopId!.Value).ToHashSet();
        var ignored = settings.ModIntents.Where(intent => intent.IsActive && installations.TryGetValue(intent.Mod, out var mod) && mod.WorkshopId.HasValue)
            .ToDictionary(intent => installations[intent.Mod].WorkshopId!.Value, intent => intent.IgnoredDependencies);

        var dependencyResult = await dependencies.EvaluateAsync(activeMods, installedWorkshop, activeMods, ignored, cancellationToken).ConfigureAwait(false);
        if (!dependencyResult.IsSuccess) return Result<ModDependencyReport>.Failure(dependencyResult.Error!);
        return Result<ModDependencyReport>.Success(dependencyResult.Value!);
    }

    private static GameLaunchRequest BuildGameLaunchRequest(ApplicationSettings settings, IReadOnlyDictionary<ModKey, ModInstallation> installations)
    {
        var gameInstallationLocation = settings.GameInstallationLocation ?? throw new InvalidOperationException("Game installation path is required to build the launch request.");
        var active = settings.ModIntents
            .Where(intent => intent.IsActive && installations.ContainsKey(intent.Mod))
            .OrderBy(intent => intent.ExplicitOrder ?? int.MaxValue)
            .ThenBy(intent => installations[intent.Mod].PackageId.Value, StringComparer.OrdinalIgnoreCase)
            .Select((intent, order) => new GameLaunchMod(intent.Mod, installations[intent.Mod].PackageId, order, installations[intent.Mod].RequiresWarOfTheChosen))
            .ToArray();

        return new GameLaunchRequest(settings.SelectedGame, gameInstallationLocation, settings.ModRootLocations, active, settings.LaunchArguments);
    }

    private static string DescribeLaunchDependencyBlock(ModDependencyReport report)
    {
        var blocking = report.Issues.Where(issue => issue.Kind is ModDependencyIssueKind.Missing or ModDependencyIssueKind.Inactive or ModDependencyIssueKind.MetadataUnavailable).ToArray();
        var counts = blocking.GroupBy(issue => issue.Kind).ToDictionary(group => group.Key, group => group.Count());
        var parts = new[]
        {
            counts.GetValueOrDefault(ModDependencyIssueKind.Missing) is var missing && missing > 0 ? $"{missing:N0} missing" : null,
            counts.GetValueOrDefault(ModDependencyIssueKind.Inactive) is var inactive && inactive > 0 ? $"{inactive:N0} inactive" : null,
            counts.GetValueOrDefault(ModDependencyIssueKind.MetadataUnavailable) is var unavailable && unavailable > 0 ? $"{unavailable:N0} metadata unavailable" : null
        }.Where(part => part is not null);
        var examples = blocking.Select(issue => issue.Message).Distinct(StringComparer.Ordinal).Take(3).ToArray();
        var detail = examples.Length == 0 ? string.Empty : $" Examples: {string.Join("; ", examples)}";
        return $"Launch blocked by dependency checks ({string.Join(", ", parts)}).{detail} Enable 'Allow launch with missing dependencies' to proceed anyway.";
    }

    public async Task<Result> ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();

        var saved = await SaveActiveDraftsAsync(cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess) return Result.Failure(saved.Error!);

        var activeSettings = saved.Value ?? throw new InvalidOperationException("Application settings were not produced after saving active drafts.");
        if (string.IsNullOrWhiteSpace(activeSettings.GameInstallationLocation)) return Result.Failure(new Error("configuration.installation_required", "Configure the selected game installation first.", ErrorKind.Validation));

        var request = BuildGameLaunchRequest(activeSettings, discoveredMods.ToDictionary(mod => mod.Key));
        var receipt = await configurationWriter.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
        Status = BuildConfigurationStatus(receipt);
        return receipt.IsSuccess ? Result.Success() : Result.Failure(receipt.Error!);
    }

    private static string BuildConfigurationStatus(Result<GameConfigurationReceipt> receipt)
    {
        if (receipt.IsSuccess)
        {
            return $"Applied configuration with {receipt.Value!.ActivePackageIds.Count:N0} mods";
        }

        return receipt.Error!.Message;
    }

    private void UpdateDraft(ModKey key, bool isActive, int? order)
    {
        modDrafts[key] = new ModIntentEdit(key, isActive, order);
        modDraftRevision++;
        RaiseDraftStateChanged();
        Status = BuildDraftStatus();
        RefreshProjectionKeys([key]);
        autoSave.Changed(ModsAutoSaveOwner);
    }

    private string BuildDraftStatus()
    {
        if (HasUnsavedModDrafts)
        {
            var suffix = UnsavedModDraftCount == 1 ? string.Empty : "s";
            return $"{UnsavedModDraftCount:N0} unsaved activation/order edit{suffix}";
        }

        return "Activation and load order match saved settings";
    }

    private Result ApplyMetadataResult(Result<ApplicationSettings> result, string successStatus)
    {
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value;
        ProjectMods(modSearchText, groupModsByCategory);
        Status = successStatus;
        return Result.Success();
    }

    private void ResetDraftsFromSettings()
    {
        modDrafts.Clear();
        if (Settings is null) return;
        var intents = Settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var modKey in discoveredMods.Select(mod => mod.Key))
        {
            intents.TryGetValue(modKey, out var intent);
            modDrafts[modKey] = new ModIntentEdit(modKey, intent?.IsActive ?? false, intent?.ExplicitOrder);
        }
        RaiseDraftStateChanged();
    }

    private bool IsUnsavedDraft(ModIntentEdit draft)
    {
        var saved = Settings!.ModIntents.FirstOrDefault(intent => intent.Mod == draft.Mod);
        return draft.IsActive != (saved?.IsActive ?? false) || draft.ExplicitOrder != saved?.ExplicitOrder;
    }

    private void RaiseDraftStateChanged()
    {
        uiDispatcher.Invoke(() =>
        {
            this.RaisePropertyChanged(nameof(UnsavedModDraftCount));
            this.RaisePropertyChanged(nameof(HasUnsavedModDrafts));
        });
    }

    private Result<int> BulkActivationFailure(string code, string message)
    {
        Status = message;
        return Result<int>.Failure(new Error(code, message, ErrorKind.Validation));
    }

    private static string BulkSkipMessage(IReadOnlyCollection<ModKey> missing, IReadOnlyCollection<ModKey> duplicates)
    {
        var reasons = new List<string>();
        if (duplicates.Count > 0) reasons.Add($"skipped {duplicates.Count:N0} unresolved/secondary duplicate{(duplicates.Count == 1 ? string.Empty : "s")}");
        if (missing.Count > 0) reasons.Add($"skipped {missing.Count:N0} missing selection{(missing.Count == 1 ? string.Empty : "s")}");
        return string.Join("; ", reasons);
    }

    public void FocusMods(IReadOnlySet<ModKey> mods)
    {
        focusedModKeys = mods.ToHashSet();
        modSearchText = string.Empty;
        groupModsByCategory = false;
        ProjectMods(string.Empty, false);
        this.RaisePropertyChanged(nameof(HasFocusedMods));
    }

    public void ClearFocusedMods() => FocusMods(new HashSet<ModKey>());

    public Result FocusDuplicateGroup(ModKey mod)
    {
        var group = duplicateReport.Groups.SingleOrDefault(group => group.Installations.Any(item => item.Key == mod));
        if (group is null) return Result.Failure(new Error("duplicates.not_duplicate", "The selected mod is not in a duplicate group.", ErrorKind.Validation));
        FocusMods(group.Installations.Select(item => item.Key).ToHashSet());
        return Result.Success();
    }

    public async Task<Result<SessionWorkshopDetails>> LoadWorkshopDetailsAsync(ModKey mod, CancellationToken cancellationToken)
    {
        var installation = discoveredMods.SingleOrDefault(item => item.Key == mod);
        if (installation?.WorkshopId is not { } id) return Result<SessionWorkshopDetails>.Failure(new Error("workshop.identity_missing", "The selected mod has no Workshop identity.", ErrorKind.Validation));
        var item = await workshopService.GetItemAsync(id, cancellationToken);
        if (!item.IsSuccess || item.Value is null) return Result<SessionWorkshopDetails>.Failure(item.Error ?? new Error("workshop.item_missing", "Steam returned no Workshop item.", ErrorKind.NotFound));
        var preview = await workshopPreviewCache.GetAsync(id, item.Value.PreviewUrl, cancellationToken);
        if (!preview.IsSuccess) return Result<SessionWorkshopDetails>.Failure(preview.Error!);
        string? author = null;
        if (item.Value.OwnerSteamId is { } owner) { var persona = await workshopService.GetPersonaNameAsync(owner, cancellationToken); if (persona.IsSuccess) author = persona.Value; }
        return Result<SessionWorkshopDetails>.Success(new SessionWorkshopDetails(author, item.Value.OwnerSteamId, item.Value.Description, item.Value.Tags ?? [], item.Value.CreatedAt, item.Value.UpdatedAt, item.Value.AddedAt, preview.Value));
    }

    public async Task<Result> AdoptWorkshopTagsAsync(ModKey mod, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var installation = discoveredMods.SingleOrDefault(item => item.Key == mod);
        if (installation?.WorkshopId is not { } id) return Result.Failure(new Error("workshop.identity_missing", "The selected mod has no Workshop identity.", ErrorKind.Validation));
        var item = await workshopService.GetItemAsync(id, cancellationToken);
        if (!item.IsSuccess || item.Value is null) return Result.Failure(item.Error ?? new Error("workshop.item_missing", "Steam returned no Workshop item.", ErrorKind.NotFound));
        return ApplyMetadataResult(await metadataService.AdoptDescriptorTaxonomyAsync(Settings, mod, null, item.Value.Tags ?? [], cancellationToken), "Workshop tags adopted");
    }

    public async Task<Result<SessionDependencyDetails>> LoadDependencyDetailsAsync(ModKey mod, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure<SessionDependencyDetails>();
        var selected = discoveredMods.SingleOrDefault(item => item.Key == mod);
        if (selected?.WorkshopId is not { } selectedId) return Result<SessionDependencyDetails>.Failure(new Error("dependencies.workshop_required", "The selected mod has no Workshop identity.", ErrorKind.Validation));
        var workshopMods = discoveredMods.Where(item => item.WorkshopId.HasValue).ToArray();
        var roots = workshopMods.Select(item => item.WorkshopId!.Value).Distinct().ToArray();
        var active = modDrafts.Values.Where(edit => edit.IsActive).Select(edit => discoveredMods.SingleOrDefault(item => item.Key == edit.Mod)?.WorkshopId).Where(id => id.HasValue).Select(id => id!.Value).ToArray();
        var ignored = Settings.ModIntents.Where(intent => intent.IgnoredDependencies.Count > 0).Select(intent => (Intent: intent, Mod: discoveredMods.SingleOrDefault(item => item.Key == intent.Mod))).Where(item => item.Mod?.WorkshopId is not null).GroupBy(item => item.Mod!.WorkshopId!.Value).ToDictionary(group => group.Key, group => (IReadOnlySet<WorkshopId>)group.SelectMany(item => item.Intent.IgnoredDependencies).ToHashSet());
        var result = await dependencies.EvaluateAsync(roots, roots, active, ignored, cancellationToken);
        if (!result.IsSuccess) return Result<SessionDependencyDetails>.Failure(result.Error!);
        var byId = workshopMods.GroupBy(item => item.WorkshopId!.Value).ToDictionary(group => group.Key, group => group.First());
        var required = TraverseRequired(selectedId, result.Value!.Graph).Select(item => Relationship(item.Parent, item.Related, item.Depth, item.Path, result.Value.Issues, byId, ignored.GetValueOrDefault(item.Parent)?.Contains(item.Related) == true)).ToArray();
        var dependents = roots.Where(root => root != selectedId).Select(root => (Root: root, Path: FindPath(root, selectedId, result.Value.Graph))).Where(item => item.Path is not null).Select(item => Relationship(item.Root, selectedId, item.Path!.Count - 1, item.Path, result.Value.Issues, byId, false)).ToArray();
        return Result<SessionDependencyDetails>.Success(new SessionDependencyDetails(required, dependents));
    }

    public async Task<Result> SetDependencyIgnoredAsync(ModKey parent, WorkshopId required, bool ignored, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var result = await modIntents.SetDependencyIgnoredAsync(Settings, parent, required, ignored, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value;
        Status = ignored ? "Dependency ignored" : "Dependency restored";
        return Result.Success();
    }

    public Result ActivateDependency(WorkshopId required)
    {
        var installation = discoveredMods.FirstOrDefault(item => item.WorkshopId == required);
        if (installation is null) return Result.Failure(new Error("dependencies.not_installed", "The dependency is not installed.", ErrorKind.NotFound));
        SetSelectedActive(new HashSet<ModKey> { installation.Key }, true);
        return Result.Success();
    }

    public Result FocusDependency(WorkshopId required)
    {
        var installation = discoveredMods.FirstOrDefault(item => item.WorkshopId == required);
        if (installation is null) return Result.Failure(new Error("dependencies.not_installed", "The dependency is not installed.", ErrorKind.NotFound));
        FocusMods(new HashSet<ModKey> { installation.Key });
        return Result.Success();
    }

    private static SessionDependencyRelationship Relationship(WorkshopId parent, WorkshopId related, int depth, IReadOnlyList<WorkshopId> path, IReadOnlyList<ModDependencyIssue> issues, IReadOnlyDictionary<WorkshopId, ModInstallation> installations, bool ignored)
    {
        installations.TryGetValue(related, out var installation);
        var issue = issues.FirstOrDefault(item => item.Parent == parent && item.Required == related);
        var status = GetDependencyStatus(ignored, issue, installation);
        return new SessionDependencyRelationship(parent, related, installation?.Name ?? $"Workshop {related.Value}", installation?.Key, status, ignored, depth, path);
    }

    private static string GetDependencyStatus(bool ignored, ModDependencyIssue? issue, ModInstallation? installation)
    {
        if (ignored) return "Ignored";
        return issue?.Kind.ToString() ?? DetermineDependencyStatusLabel(installation);
    }

    public sealed record PreferenceSaveRequest(
        IReadOnlyList<LaunchArgument> Arguments,
        IReadOnlyList<string> Roots,
        bool AllowMissingDependencies,
        bool CloseAfterLaunch,
        WorkshopStartupRefreshPolicy StartupRefresh,
        ThemePreference Theme,
        bool AllowMultipleInstances,
        bool CheckForUpdates,
        UpdateChannelPreference UpdateChannel,
        decimal TextScale,
        decimal IconScale);

    private static string DetermineDependencyStatusLabel(ModInstallation? installation) => installation is null ? "Missing" : "Satisfied";

    private static IEnumerable<(WorkshopId Parent, WorkshopId Related, int Depth, IReadOnlyList<WorkshopId> Path)> TraverseRequired(WorkshopId root, IReadOnlyDictionary<WorkshopId, IReadOnlyList<WorkshopId>> graph)
    {
        var queue = new Queue<(WorkshopId Node, IReadOnlyList<WorkshopId> Path)>();
        queue.Enqueue((root, new[] { root }));
        var seen = new HashSet<(WorkshopId, WorkshopId)>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in graph.GetValueOrDefault(current.Node) ?? [])
            {
                if (!seen.Add((current.Node, child))) continue;
                var path = current.Path.Append(child).ToArray();
                yield return (current.Node, child, path.Length - 1, path);
                if (!current.Path.Contains(child)) queue.Enqueue((child, path));
            }
        }
    }

    private static IReadOnlyList<WorkshopId>? FindPath(WorkshopId root, WorkshopId target, IReadOnlyDictionary<WorkshopId, IReadOnlyList<WorkshopId>> graph)
    {
        var queue = new Queue<IReadOnlyList<WorkshopId>>();
        queue.Enqueue(new[] { root });
        var seen = new HashSet<WorkshopId> { root };

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            foreach (var child in graph.GetValueOrDefault(path[^1]) ?? [])
            {
                var next = path.Append(child).ToArray();
                if (child == target) return next;
                if (seen.Add(child)) queue.Enqueue(next);
            }
        }

        return null;
    }

    public async Task<Result> RefreshWorkshopStatesAsync(IProgress<WorkshopOperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!await TryEnterWorkshopAsync(cancellationToken)) return WorkshopBusy();
        SetWorkshopAvailability(WorkshopConnectionState.Connecting);
        var operationRevision = Interlocked.Increment(ref workshopProgressRevision);
        var forwarding = new Progress<WorkshopOperationProgress>(update =>
        {
            if (Volatile.Read(ref workshopProgressRevision) != operationRevision) return;
            if (update.State is not null) ApplyWorkshopProgress(update.State, operationRevision);
            progress?.Report(update);
        });
        try
        {
            var result = await workshopOperations.RefreshAsync(discoveredMods, forwarding, cancellationToken);
            Interlocked.CompareExchange(ref workshopProgressRevision, operationRevision + 1, operationRevision);
            ApplyWorkshopOutcomes(result.Items);
            Status = DescribeWorkshopResult("Workshop state refreshed", result);
            ObserveWorkshopAvailability(result);
            return result.IsSuccess ? Result.Success() : Result.Failure(new Error("workshop.refresh_partial", Status, DetermineWorkshopBatchErrorKind(result)));
        }
        finally
        {
            Interlocked.CompareExchange(ref workshopProgressRevision, operationRevision + 1, operationRevision);
            ExitWorkshop();
        }
    }

    public async Task<Result> DownloadWorkshopUpdatesAsync(IReadOnlySet<ModKey> mods, IProgress<WorkshopOperationProgress>? progress, CancellationToken cancellationToken)
    {
        var selected = discoveredMods.Where(mod => mods.Contains(mod.Key) && mod.WorkshopId.HasValue).ToArray();
        if (selected.Length == 0) return Result.Failure(new Error("workshop.selection_empty", "Select at least one Workshop mod.", ErrorKind.Validation));
        if (!await TryEnterWorkshopAsync(cancellationToken)) return WorkshopBusy();
        SetWorkshopAvailability(WorkshopConnectionState.Connecting);
        var operationRevision = Interlocked.Increment(ref workshopProgressRevision);
        double aggregate = 0;
        var forwarding = new Progress<WorkshopOperationProgress>(update =>
        {
            if (Volatile.Read(ref workshopProgressRevision) != operationRevision) return;
            if (update.State is not null) ApplyWorkshopProgress(update.State, operationRevision);
            if (update.BytesTotal is > 0) aggregate = Math.Max(aggregate, (double)update.BytesDownloaded / update.BytesTotal.Value);
            Status = BuildWorkshopProgressStatus(update, aggregate);
            progress?.Report(update);
        });
        try
        {
            var result = await workshopOperations.DownloadUpdatesAsync(selected, WorkshopDownloadOptions.Default, forwarding, cancellationToken);
            Interlocked.CompareExchange(ref workshopProgressRevision, operationRevision + 1, operationRevision);
            ApplyWorkshopOutcomes(result.Items);
            await RefreshModsAndConfigurationsAsync(CancellationToken.None);
            ApplyWorkshopOutcomes(result.Items);
            Status = DescribeWorkshopResult(BuildDownloadSummaryLabel(result.ObservationCancelled), result);
            ObserveWorkshopAvailability(result);
            return result.IsSuccess ? Result.Success() : Result.Failure(new Error("workshop.download_partial", Status, DetermineDownloadErrorKind(result.ObservationCancelled)));
        }
        finally
        {
            Interlocked.CompareExchange(ref workshopProgressRevision, operationRevision + 1, operationRevision);
            ExitWorkshop();
        }
    }

    public async Task<Result> SubscribeRetainedAsync(WorkshopId id, CancellationToken cancellationToken)
    {
        if (!await TryEnterWorkshopAsync(cancellationToken)) return WorkshopBusy();
        try
        {
            var result = await subscriptions.SubscribeAsync([id], cancellationToken);
            await RefreshModsAndConfigurationsAsync(CancellationToken.None);
            await RefreshProfilesAsync(CancellationToken.None);
            var item = result.Items.Single();
            var workshopStatus = BuildRetainedWorkshopStatus(item);
            uiDispatcher.Invoke(() =>
            {
                retainedWorkshopStatuses[id] = workshopStatus;
                ProjectModsCore(modSearchText, groupModsByCategory);
            });
            Status = BuildRetainedSubscriptionStatus(item, id, result);
            return result.IsSuccess ? Result.Success() : Result.Failure(item.Outcome.Error!);
        }
        finally { ExitWorkshop(); }
    }

    private static string BuildRetainedWorkshopStatus(WorkshopMutationOutcome item)
    {
        if (item.Subscribed)
        {
            if (item.DownloadRequested)
            {
                return "Subscribed; waiting for Steam download";
            }

            return item.DownloadRequestOutcome is { Error: { Message: var message } }
                ? $"Subscribed; download request failed: {message}"
                : "Subscribed; download request failed";
        }

        return item.Outcome.Error?.Message ?? "Subscription failed";
    }

    private static string BuildRetainedSubscriptionStatus(WorkshopMutationOutcome item, WorkshopId id, WorkshopMutationResult result)
    {
        if (item.Subscribed && !item.DownloadRequested)
        {
            return item.DownloadRequestOutcome is { Error: { Message: var message } }
                ? $"Subscribed to Workshop item {id.Value}, but the download request failed: {message}"
                : $"Subscribed to Workshop item {id.Value}, but the download request failed";
        }

        if (result.IsSuccess)
        {
            return $"Subscribed to Workshop item {id.Value}; waiting for Steam download";
        }

        return item.Outcome.Error?.Message ?? "Subscription failed";
    }

    public async Task<Result> UnsubscribeRetainingIntentAsync(IReadOnlySet<ModKey> mods, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        if (!await TryEnterWorkshopAsync(cancellationToken)) return WorkshopBusy();
        try
        {
            var result = await subscriptions.UnsubscribeRetainingIntentAsync(Settings, discoveredMods, mods, cancellationToken);
            if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
            Settings = result.Value.Settings;
            await RefreshModsAndConfigurationsAsync(CancellationToken.None);
            await RefreshProfilesAsync(CancellationToken.None);
            Status = BuildUnsubscribeStatus(result.Value.Mutations.IsSuccess);
            return result.Value.Mutations.IsSuccess ? Result.Success() : Result.Failure(new Error("workshop.unsubscribe_partial", Status, ErrorKind.ExternalService));
        }
        finally { ExitWorkshop(); }
    }

    public async Task<Result> RemoveRetainedIntentAsync(WorkshopId id, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var result = await subscriptions.RemoveRetainedIntentAsync(Settings, id, cancellationToken);
        if (!result.IsSuccess) return Result.Failure(result.Error!);
        Settings = result.Value; ProjectMods(modSearchText, groupModsByCategory); Status = "Removed retained Workshop intent"; return Result.Success();
    }

    public Task<Result<ModRemovalPreview>> PreviewManualRemovalAsync(ModKey mod, CancellationToken cancellationToken) => removalFilesystem.PreviewAsync(mod, Settings?.ModRootLocations ?? [], cancellationToken);

    public async Task<Result> ConfirmManualRemovalAsync(ModRemovalPreview preview, CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var deleted = await removalFilesystem.DeleteConfirmedAsync(preview, cancellationToken); if (!deleted.IsSuccess) return deleted;
        var removed = await modIntents.RemoveAsync(Settings, preview.Mod, cancellationToken); if (!removed.IsSuccess) return Result.Failure(removed.Error!);
        Settings = removed.Value; await RefreshModsAndConfigurationsAsync(CancellationToken.None); await RefreshProfilesAsync(CancellationToken.None); Status = "Manual mod removed"; return Result.Success();
    }

    public async Task<Result> RefreshConfigurationDocumentsAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return NotInitializedFailure();
        var result = await configurationCatalog.ListAsync(discoveredMods, Settings.SelectedGame, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        ConfigurationDocuments.Clear();
        foreach (var document in result.Value!) ConfigurationDocuments.Add(document);
        return Result.Success();
    }

    private IReadOnlySet<ModKey> EffectiveActiveModKeys() => modDrafts.Values.Where(draft => draft.IsActive).Select(draft => draft.Mod).ToHashSet();

    private void ApplyConflicts(ModConflictReport report)
    {
        uiDispatcher.Invoke(() => ApplyConflictsCore(report));
    }

    private void ApplyConflictsCore(ModConflictReport report)
    {
        conflictingMods.Clear();
        Conflicts.Clear();
        var names = discoveredMods.ToDictionary(mod => mod.Key, mod => mod.Name);
        foreach (var conflict in report.Conflicts)
        {
            foreach (var participant in conflict.Participants) conflictingMods.Add(participant);
            Conflicts.Add(new SessionConflict(conflict.Key, conflict.Kind.ToString(), conflict.Subject,
                string.Join(", ", conflict.Participants.Select(key => names.GetValueOrDefault(key, key.ToString()))), conflict.Participants, conflict.Facts));
        }
        this.RaisePropertyChanged(nameof(Conflicts));
    }

    private void ApplyWorkshopOutcomes(IEnumerable<WorkshopModOutcome> outcomes)
    {
        uiDispatcher.Invoke(() =>
        {
            var affected = new HashSet<ModKey>();
            foreach (var outcome in outcomes)
            {
                if (!modDrafts.ContainsKey(outcome.Mod)) continue;
                if (outcome.State is not null) workshopStates[outcome.Mod] = outcome.State;
                if (outcome.Outcome.IsSuccess) workshopErrors.Remove(outcome.Mod);
                else workshopErrors[outcome.Mod] = outcome.Outcome.Error!.Message;
                affected.Add(outcome.Mod);
            }
            RefreshProjectionKeysCore(affected);
        });
    }

    private async Task ApplyStartupWorkshopPolicyAsync(CancellationToken cancellationToken)
    {
        if (Settings is null || Settings.WorkshopStartupRefresh == WorkshopStartupRefreshPolicy.Manual) return;
        IReadOnlyList<ModInstallation> selected = Settings.WorkshopStartupRefresh == WorkshopStartupRefreshPolicy.AllMods
            ? discoveredMods.Where(mod => mod.WorkshopId.HasValue).ToArray()
            : discoveredMods.Where(mod => mod.WorkshopId.HasValue && Settings.ModIntents.Any(intent => intent.Mod == mod.Key && intent.IsActive)).ToArray();
        if (selected.Count == 0) return;
        if (!await TryEnterWorkshopAsync(cancellationToken)) return;
        SetWorkshopAvailability(WorkshopConnectionState.Connecting);
        try
        {
            var result = await workshopOperations.RefreshAsync(selected, null, cancellationToken);
            ApplyWorkshopOutcomes(result.Items);
            Status = DescribeWorkshopResult("Workshop state checked during startup", result);
            ObserveWorkshopAvailability(result);
        }
        finally { ExitWorkshop(); }
    }

    private void ApplyWorkshopProgress(WorkshopModState state, long operationRevision)
    {
        uiDispatcher.Invoke(() =>
        {
            if (Volatile.Read(ref workshopProgressRevision) != operationRevision) return;
            var affected = discoveredMods
                .Where(mod => mod.WorkshopId == state.WorkshopId)
                .Select(mod => mod.Key)
                .ToArray();
            foreach (var key in affected)
            {
                workshopStates[key] = state with { Mod = key };
                workshopErrors.Remove(key);
            }
            RefreshProjectionKeysCore(affected);
        });
    }

    private async Task<bool> TryEnterWorkshopAsync(CancellationToken cancellationToken)
    {
        var entered = await workshopGate.WaitAsync(0, cancellationToken);
        if (entered) uiDispatcher.Invoke(() => this.RaisePropertyChanged(nameof(IsWorkshopBusy)));
        return entered;
    }

    private void ExitWorkshop()
    {
        workshopGate.Release();
        uiDispatcher.Invoke(() => this.RaisePropertyChanged(nameof(IsWorkshopBusy)));
    }

    private static Result WorkshopBusy() => Result.Failure(new Error("workshop.operation_in_progress", "Another Workshop operation is already in progress.", ErrorKind.Conflict));

    private static ErrorKind DetermineWorkshopBatchErrorKind(WorkshopBatchResult result)
    {
        if (result.IsPartialSuccess) return ErrorKind.ExternalService;

        var error = result.Items.FirstOrDefault(item => !item.Outcome.IsSuccess)?.Outcome.Error;
        return error?.Kind ?? ErrorKind.ExternalService;
    }

    private static string BuildWorkshopProgressStatus(WorkshopOperationProgress update, double aggregate)
    {
        if (update.BytesTotal is > 0)
        {
            return $"Downloading Workshop content: {aggregate:P0}";
        }

        return $"Monitoring Workshop downloads: {update.CompletedItems:N0}/{update.TotalItems:N0}";
    }

    private static string BuildDownloadSummaryLabel(bool observationCancelled) => observationCancelled ? "Stopped monitoring Workshop downloads; Steam may continue" : "Workshop downloads completed";

    private static ErrorKind DetermineDownloadErrorKind(bool observationCancelled) => observationCancelled ? ErrorKind.Cancelled : ErrorKind.ExternalService;

    private static string BuildUnsubscribeStatus(bool success) => success ? "Unsubscribed and retained mod intent" : "Some Workshop unsubscribe operations failed";

    private void SetWorkshopAvailability(WorkshopConnectionState state, Error? error = null) =>
        WorkshopAvailability = new WorkshopAvailability(state, DateTimeOffset.UtcNow, error?.Message,
            state == WorkshopConnectionState.Unavailable ? "Start Steam, sign in, then retry the Workshop operation." : null);

    private void ObserveWorkshopAvailability(WorkshopBatchResult result)
    {
        var failure = result.Items.FirstOrDefault(item => !item.Outcome.IsSuccess)?.Outcome.Error;
        SetWorkshopAvailability(result.Items.Any(item => item.Outcome.IsSuccess) ? WorkshopConnectionState.Connected : WorkshopConnectionState.Unavailable, failure);
    }

    private static string DescribeWorkshopResult(string success, WorkshopBatchResult result)
    {
        var failures = result.Items.Count(item => !item.Outcome.IsSuccess);
        return failures == 0 ? $"{success}: {result.Items.Count:N0} mods" : $"{success}: {result.Items.Count - failures:N0} succeeded, {failures:N0} failed";
    }

    private async Task RefreshDependencyStatusesAsync(CancellationToken cancellationToken)
    {
        var input = await uiDispatcher.InvokeAsync(() => Settings is null ? null : new DependencyStatusInput(Settings, discoveredMods.ToArray(), EffectiveActiveModKeys()), cancellationToken).ConfigureAwait(false);
        if (input is null) return;
        var statuses = new Dictionary<ModKey, DependencyStatus>();
        var installations = input.DiscoveredMods.ToDictionary(mod => mod.Key);
        var activeWorkshopMods = input.ActiveMods.Where(installations.ContainsKey).Select(key => installations[key]).Where(mod => mod.WorkshopId.HasValue).ToArray();
        var activeIds = activeWorkshopMods.Select(mod => mod.WorkshopId!.Value).ToHashSet();
        if (activeIds.Count == 0)
        {
            await uiDispatcher.InvokeAsync(dependencyStatuses.Clear, cancellationToken).ConfigureAwait(false);
            return;
        }
        var installedIds = input.DiscoveredMods.Where(mod => mod.WorkshopId.HasValue).Select(mod => mod.WorkshopId!.Value).ToHashSet();
        var intents = input.Settings.ModIntents.ToDictionary(intent => intent.Mod);
        var ignored = activeWorkshopMods.ToDictionary(mod => mod.WorkshopId!.Value,
            mod => intents.GetValueOrDefault(mod.Key)?.IgnoredDependencies ?? new HashSet<WorkshopId>());
        var result = await dependencies.EvaluateAsync(activeIds, installedIds, activeIds, ignored, cancellationToken);
        if (!result.IsSuccess) return;
        foreach (var mod in activeWorkshopMods)
        {
            var id = mod.WorkshopId!.Value;
            var related = result.Value!.Issues.Where(issue => issue.Path.Count > 0 && issue.Path[0] == id).ToArray();
            statuses[mod.Key] = DetermineDependencyStatus(related);
        }
        await uiDispatcher.InvokeAsync(() =>
        {
            dependencyStatuses.Clear();
            foreach (var (key, dependencyState) in statuses) dependencyStatuses[key] = dependencyState;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static DependencyStatus DetermineDependencyStatus(IReadOnlyList<ModDependencyIssue> related)
    {
        if (related.Any(issue => issue.Kind is ModDependencyIssueKind.Missing or ModDependencyIssueKind.Inactive))
        {
            return DependencyStatus.Missing;
        }

        if (related.Any(issue => issue.Kind == ModDependencyIssueKind.MetadataUnavailable))
        {
            return DependencyStatus.Unknown;
        }

        return DependencyStatus.Satisfied;
    }

    private sealed record ModDraftSaveCapture(ApplicationSettings Settings, long Revision, IReadOnlyList<ModIntentEdit> Drafts, IReadOnlyList<ModInstallation> DiscoveredMods, ModDuplicateReport DuplicateReport);
    private sealed record ModGridSaveCapture(ApplicationSettings Settings, long Revision, ModGridPreferences Preferences);
    private sealed record DependencyStatusInput(ApplicationSettings Settings, IReadOnlyList<ModInstallation> DiscoveredMods, IReadOnlySet<ModKey> ActiveMods);
    private sealed record ConflictAnalysisInput(IReadOnlyList<ModInstallation> DiscoveredMods, IReadOnlySet<ModKey> ActiveMods);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        modProjectionStore?.Dispose();
        autoSave.Dispose();
    }
}

public sealed record SessionProfile(ProfileId Id, string Name, GameVariant GameVariant, int ModCount);
public sealed record SessionUpdateCheck(bool Manual, DateTimeOffset CheckedAt, UpdateChannelPreference Channel, UpdateCheckResult? Result, Error? Error)
{
    public string Details
    {
        get
        {
            if (Error is not null)
            {
                return $"Update check failed {CheckedAt.LocalDateTime:g}: {Error.Message}";
            }

            if (Result is null)
            {
                return "No update check has run in this session.";
            }

            var summary = $"{(Manual ? "Manual" : "Startup")} {Channel} update check {CheckedAt.LocalDateTime:g}: {Result.Message}";
            return Result.Release is null ? summary : $"{summary}\n{Result.Release.Name}\n{Result.Release.Notes}";
        }
    }
}
public sealed record SessionConflict(string Key, string Kind, string Subject, string ParticipantsText, IReadOnlyList<ModKey> Participants, IReadOnlyList<ModConflictFact> Facts);
public sealed record SessionDependencyDetails(IReadOnlyList<SessionDependencyRelationship> Required, IReadOnlyList<SessionDependencyRelationship> Dependents);
public sealed record SessionDependencyRelationship(WorkshopId Parent, WorkshopId WorkshopId, string Name, ModKey? Mod, string Status, bool IsIgnored, int Depth, IReadOnlyList<WorkshopId> Path)
{
    public string Scope => Depth == 1 ? "Direct" : $"Transitive ({Depth})";
}
public sealed record SessionWorkshopDetails(string? Author, ulong? OwnerSteamId, string? Description, IReadOnlyList<string> Tags, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, DateTimeOffset? AddedAt, string? PreviewImagePath);
public sealed record SessionMissingProfileItem(WorkshopId WorkshopId, string PackageId);
public sealed record SessionModMetadata(ModKey Key, string? ManualName, string? Note, bool IsHidden, CategoryId? Category, IReadOnlySet<TagId> Tags);
public enum WorkshopConnectionState { Unknown, Connecting, Connected, Unavailable }
public sealed record WorkshopAvailability(WorkshopConnectionState State, DateTimeOffset? LastCheckedAt, string? Error, string? Remediation)
{
    public static WorkshopAvailability Unknown { get; } = new(WorkshopConnectionState.Unknown, null, null, null);
}

public sealed class SessionModRow : ReactiveObject
{
    private readonly Action<ModKey, bool, int?>? update;
    private bool? isActive;
    private int? order;
    private string workshop = string.Empty;
    private double? downloadProgress;
    private bool isDownloading;
    private string name;
    private string state;
    private int? count;
    private bool isExpanded;
    private bool requiresWarOfTheChosen;
    private PackageId? packageId;
    private WorkshopId? workshopId;
    private DuplicateStatus duplicateStatus;
    private string description = string.Empty;
    private string descriptorTags = string.Empty;
    private string descriptorCategory = string.Empty;
    private string readmePath = string.Empty;
    private string previewImagePath = string.Empty;

    private SessionModRow(SessionModRowData data, Action<ModKey, bool, int?>? update)
    {
        Key = data.Key;
        GroupKey = data.GroupKey;
        name = data.Name;
        isActive = data.Active;
        order = data.ExplicitOrder;
        state = data.State;
        count = data.Count;
        isExpanded = data.IsExpanded;
        requiresWarOfTheChosen = data.RequiresWarOfTheChosen;
        packageId = data.PackageId;
        workshopId = data.WorkshopId;
        duplicateStatus = data.DuplicateStatus;
        this.update = update;
        ApplyWorkshop(data.WorkshopState, data.WorkshopError);
    }

    public ModKey? Key { get; }
    public ModGridGroupKey? GroupKey { get; }
    public bool IsGroup => Key is null;
    public string Name { get => name; private set => this.RaiseAndSetIfChanged(ref name, value); }
    public string State
    {
        get => state;
        private set
        {
            if (state == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref state, value);
            this.RaisePropertyChanged(nameof(CanActivate));
            this.RaisePropertyChanged(nameof(IsRetainedMissing));
        }
    }
    public int? Count { get => count; private set => this.RaiseAndSetIfChanged(ref count, value); }
    public bool IsExpanded
    {
        get => isExpanded;
        private set
        {
            if (isExpanded == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref isExpanded, value);
            this.RaisePropertyChanged(nameof(GroupToggleLabel));
        }
    }
    public string GroupToggleLabel => IsExpanded ? "Collapse group" : "Expand group";
    public bool RequiresWarOfTheChosen { get => requiresWarOfTheChosen; private set => this.RaiseAndSetIfChanged(ref requiresWarOfTheChosen, value); }
    public PackageId? PackageId { get => packageId; private set => this.RaiseAndSetIfChanged(ref packageId, value); }
    public WorkshopId? WorkshopId
    {
        get => workshopId;
        private set
        {
            if (workshopId == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref workshopId, value);
            this.RaisePropertyChanged(nameof(WorkshopUrl));
            this.RaisePropertyChanged(nameof(IsRetainedMissing));
        }
    }
    public DuplicateStatus DuplicateStatus
    {
        get => duplicateStatus;
        private set
        {
            if (duplicateStatus == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref duplicateStatus, value);
            this.RaisePropertyChanged(nameof(CanActivate));
        }
    }
    public bool CanActivate => !IsGroup && State != "Missing" && DuplicateStatus is DuplicateStatus.None or DuplicateStatus.Preferred;
    public bool IsRetainedMissing => State == "Missing" && WorkshopId.HasValue;
    public string Source => Key?.Source.ToString() ?? string.Empty;
    public string Location => Key?.LocationIdentity ?? string.Empty;
    public string Description { get => description; private set => this.RaiseAndSetIfChanged(ref description, value); }
    public string DescriptorTags { get => descriptorTags; private set => this.RaiseAndSetIfChanged(ref descriptorTags, value); }
    public string DescriptorCategory { get => descriptorCategory; private set => this.RaiseAndSetIfChanged(ref descriptorCategory, value); }
    public string ReadmePath { get => readmePath; private set => this.RaiseAndSetIfChanged(ref readmePath, value); }
    public string PreviewImagePath { get => previewImagePath; private set => this.RaiseAndSetIfChanged(ref previewImagePath, value); }
    public string WorkshopUrl => WorkshopId is { } id ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={id.Value}" : string.Empty;
    public string Workshop { get => workshop; private set => this.RaiseAndSetIfChanged(ref workshop, value); }
    public double? DownloadProgress { get => downloadProgress; private set => this.RaiseAndSetIfChanged(ref downloadProgress, value); }
    public bool IsDownloading { get => isDownloading; private set => this.RaiseAndSetIfChanged(ref isDownloading, value); }
    public bool? IsActive
    {
        get => isActive;
        set
        {
            if (value == true && !CanActivate)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref isActive, value);
            if (Key is { } key && value.HasValue)
            {
                update?.Invoke(key, value.Value, order);
            }
        }
    }
    public int? Order
    {
        get => order;
        set
        {
            this.RaiseAndSetIfChanged(ref order, value);
            if (Key is { } key)
            {
                update?.Invoke(key, isActive ?? false, value);
            }
        }
    }

    public void ApplyWorkshop(WorkshopModState? state, string? error)
    {
        IsDownloading = state?.Update == UpdateStatus.Downloading;
        DownloadProgress = state?.Download?.Fraction is { } fraction ? fraction * 100 : null;
        Workshop = error is not null ? $"Unavailable: {error}" : GetWorkshopStatus(state);
    }

    private static string GetWorkshopStatus(WorkshopModState? state)
    {
        if (state is null) return string.Empty;

        return state.Update switch
        {
            UpdateStatus.Current => "Current",
            UpdateStatus.Available => "Update available",
            UpdateStatus.Downloading when state.Download?.Fraction is { } downloadFraction => $"Downloading {downloadFraction:P0}",
            UpdateStatus.Downloading => state.RawState.HasFlag(WorkshopItemState.DownloadPending) ? "Queued" : "Downloading",
            _ => "Unknown"
        };
    }

    internal void RefreshGroup(string groupName, int itemCount, bool expanded)
    {
        Name = groupName;
        Count = itemCount;
        IsExpanded = expanded;
    }

    internal void RefreshMod(ModGridItem item, ModInstallation installation, string semanticState, WorkshopModState? workshopState, string? workshopError)
    {
        Name = item.DisplayName;
        SetProjectedDraft(item.IsActive, item.ExplicitOrder);
        State = semanticState;
        RequiresWarOfTheChosen = item.RequiresWarOfTheChosen;
        PackageId = item.PackageId;
        WorkshopId = item.WorkshopId;
        DuplicateStatus = item.Status.Duplicate;
        ApplyInstallationMetadata(installation);
        ApplyWorkshop(workshopState, workshopError);
    }

    internal void RefreshRetained(RetainedWorkshopItem item, string? workshopStatus)
    {
        Name = item.Name;
        PackageId = item.PackageId;
        WorkshopId = item.WorkshopId;
        Workshop = workshopStatus ?? "Retained intent; subscription/download state unknown";
    }

    private void SetProjectedDraft(bool active, int? explicitOrder)
    {
        this.RaiseAndSetIfChanged(ref isActive, active, nameof(IsActive));
        this.RaiseAndSetIfChanged(ref order, explicitOrder, nameof(Order));
    }

    private void ApplyInstallationMetadata(ModInstallation installation)
    {
        Description = installation.Metadata?.Description ?? string.Empty;
        DescriptorTags = string.Join(", ", installation.Metadata?.DescriptorTags ?? []);
        DescriptorCategory = installation.Metadata?.DescriptorCategory ?? string.Empty;
        ReadmePath = installation.Metadata?.ReadmePath ?? string.Empty;
        PreviewImagePath = installation.Metadata?.PreviewImagePath ?? string.Empty;
    }

    public static SessionModRow Group(ModGridGroupKey key, string name, int count, bool isExpanded) => new(new SessionModRowData(null, key, name, null, null, string.Empty, count, isExpanded, false, null, null, DuplicateStatus.None, null, null), null);
    public static SessionModRow Mod(ModGridItem item, ModInstallation installation, string state, WorkshopModState? workshop, string? workshopError, Action<ModKey, bool, int?> update)
    {
        var row = new SessionModRow(new SessionModRowData(item.Key, null, item.DisplayName, item.IsActive, item.ExplicitOrder, state, null, false, item.RequiresWarOfTheChosen, item.PackageId, item.WorkshopId, item.Status.Duplicate, workshop, workshopError), update);
        row.ApplyInstallationMetadata(installation);
        return row;
    }
    public static SessionModRow Retained(RetainedWorkshopItem item, string? workshopStatus = null) => new(new SessionModRowData(item.LastKnownKey, null, item.Name, false, null, "Missing", null, false, false, item.PackageId, item.WorkshopId, DuplicateStatus.None, null, workshopStatus ?? "Retained intent; subscription/download state unknown"), null);

    private sealed record SessionModRowData(
        ModKey? Key,
        ModGridGroupKey? GroupKey,
        string Name,
        bool? Active,
        int? ExplicitOrder,
        string State,
        int? Count,
        bool IsExpanded,
        bool RequiresWarOfTheChosen,
        PackageId? PackageId,
        WorkshopId? WorkshopId,
        DuplicateStatus DuplicateStatus,
        WorkshopModState? WorkshopState,
        string? WorkshopError);
}
