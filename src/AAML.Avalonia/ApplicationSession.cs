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

namespace AAML.Avalonia;

public sealed class ApplicationSession(ISettingsBootstrapper bootstrapper, IModCatalogSource catalog, IGameLaunchCoordinator launchCoordinator, IGameConfigurationWriter configurationWriter, ISteamSettingsIntegrator steamSettings, IModIntentService modIntents, IProfileService profileService, IProfileInterchange profileInterchange, ILegacyProfileImportService legacyProfileImport, IModDependencyService dependencies, IModMetadataService metadataService, IModConflictService conflictService, IConfigurationDocumentCatalog configurationCatalog, IWorkshopOperationCoordinator workshopOperations, IWorkshopSubscriptionCoordinator subscriptions, IModRemovalFilesystem removalFilesystem, IModDuplicateAnalyzer duplicateAnalyzer, IDuplicatePreferenceService duplicatePreferences, IWorkshopService workshopService, IWorkshopPreviewCache workshopPreviewCache, IUpdateCheckService updateChecks, IApplicationDiagnostics diagnostics) : ReactiveObject
{
    private readonly SemaphoreSlim initialization = new(1, 1);
    private readonly SemaphoreSlim workshopGate = new(1, 1);
    private ApplicationSettings? settings;
    private SettingsOrigin? origin;
    private string status = "Not initialized";
    private bool initialized;
    private IReadOnlyList<ModInstallation> discoveredMods = [];
    private readonly Dictionary<ModKey, ModIntentEdit> modDrafts = [];
    private readonly Dictionary<ModKey, DependencyStatus> dependencyStatuses = [];
    private readonly HashSet<ModKey> conflictingMods = [];
    private readonly Dictionary<ModKey, WorkshopModState> workshopStates = [];
    private readonly Dictionary<ModKey, string> workshopErrors = [];
    private readonly Dictionary<WorkshopId, string> retainedWorkshopStatuses = [];
    private WorkshopAvailability workshopAvailability = WorkshopAvailability.Unknown;
    private ModDuplicateReport duplicateReport = new([], new Dictionary<ModKey, DuplicateStatus>());
    private IReadOnlySet<ModKey> focusedModKeys = new HashSet<ModKey>();
    private string modSearchText = string.Empty;
    private bool groupModsByCategory;
    private bool includeHidden = true;
    private ModGridSemanticState? modStateFilter;
    private readonly HashSet<ModGridGroupKey> collapsedModGroups = [];

    public ApplicationSettings? Settings { get => settings; private set => this.RaiseAndSetIfChanged(ref settings, value); }
    public SettingsOrigin? Origin { get => origin; private set => this.RaiseAndSetIfChanged(ref origin, value); }
    public string Status { get => status; private set => this.RaiseAndSetIfChanged(ref status, value); }
    public ObservableCollection<SessionModRow> ModRows { get; } = [];
    public ObservableCollection<SessionProfile> Profiles { get; } = [];
    public ObservableCollection<SessionConflict> Conflicts { get; } = [];
    public ObservableCollection<ConfigurationDocumentSummary> ConfigurationDocuments { get; } = [];
    public bool HasFocusedMods => focusedModKeys.Count > 0;
    public int UnsavedModDraftCount => Settings is null ? 0 : modDrafts.Values.Count(IsUnsavedDraft);
    public bool HasUnsavedModDrafts => UnsavedModDraftCount > 0;
    public WorkshopAvailability WorkshopAvailability { get => workshopAvailability; private set => this.RaiseAndSetIfChanged(ref workshopAvailability, value); }
    public bool IsWorkshopBusy => workshopGate.CurrentCount == 0;
    public IReadOnlyList<Category> Categories => Settings?.Categories ?? [];
    public IReadOnlyList<Tag> Tags => Settings?.Tags ?? [];
    public ReleaseInfo? LatestRelease { get; private set; }
    public IReadOnlyList<ModInstallation> DiscoveredMods => discoveredMods;

    public async Task<Result> AcceptMigratedSettingsAsync(ApplicationSettings migrated, CancellationToken cancellationToken)
    {
        Settings = migrated;
        Status = "Migration applied";
        return await RefreshModsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> InitializeAsync(CancellationToken cancellationToken)
    {
        await initialization.WaitAsync(cancellationToken);
        try
        {
            if (initialized) return Result.Success();
            diagnostics.Write(LocalLogLevel.Information, "application.initialization_started", "Application initialization started.");
            Status = "Loading settings";
            var result = await bootstrapper.InitializeAsync(cancellationToken);
            if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
            Settings = result.Value!.Settings;
            var grid = Settings.ModGrid ?? ModGridPreferences.Default;
            includeHidden = grid.IncludeHidden;
            modStateFilter = grid.StateFilter;
            groupModsByCategory = grid.GroupByCategory;
            collapsedModGroups.Clear(); foreach (var key in grid.CollapsedGroups) collapsedModGroups.Add(key);
            Origin = result.Value.Origin;
            initialized = true;
            Status = $"Settings {Origin}";
            if (string.IsNullOrWhiteSpace(Settings.GameInstallationLocation))
            {
                var detected = await steamSettings.DiscoverAndApplyAsync(Settings, cancellationToken);
                if (detected.IsSuccess)
                {
                    Settings = detected.Value!.Settings;
                    Status = "Detected Steam installation";
                }
                else Status = $"Steam detection: {detected.Error!.Message}";
            }
            var refreshed = await RefreshModsAsync(cancellationToken);
            if (!refreshed.IsSuccess) return refreshed;
            var profiles = await RefreshProfilesAsync(cancellationToken);
            if (!profiles.IsSuccess) return profiles;
            await ApplyStartupWorkshopPolicyAsync(cancellationToken);
            diagnostics.Write(LocalLogLevel.Information, "application.initialization_completed", "Application initialization completed.", new Dictionary<string, string> { ["game"] = Settings.SelectedGame.ToString(), ["origin"] = Origin.ToString()! });
            if (Settings.CheckForUpdates) await CheckForUpdatesAsync(false, cancellationToken).ConfigureAwait(false);
            await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        finally { initialization.Release(); }
    }

    public async Task<Result<UpdateCheckResult>> CheckForUpdatesAsync(bool manual, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result<UpdateCheckResult>.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        diagnostics.Write(LocalLogLevel.Information, "update.check_started", manual ? "Manual update check started." : "Startup update check started.");
        var result = await updateChecks.CheckAsync(Settings.UpdateChannel, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            diagnostics.Write(LocalLogLevel.Warning, "update.check_failed", result.Error!.Message, new Dictionary<string, string> { ["code"] = result.Error.Code });
            if (manual) Status = result.Error.Message;
            await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        LatestRelease = result.Value!.Status == UpdateCheckStatus.UpdateAvailable ? result.Value.Release : null;
        if (manual || LatestRelease is not null) Status = result.Value.Message;
        this.RaisePropertyChanged(nameof(LatestRelease));
        await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<Result> SelectGameAsync(GameVariant game, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        var result = await bootstrapper.SelectGameAsync(Settings, game, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value;
        Status = $"Selected {game}";
        return await RefreshModsAsync(cancellationToken);
    }

    public async Task<Result> SetGameInstallationAsync(string installationPath, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        var result = await bootstrapper.SetGameInstallationAsync(Settings, installationPath, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value;
        Status = "Game installation saved";
        return Result.Success();
    }

    public async Task<Result> SavePreferencesAsync(IReadOnlyList<LaunchArgument> arguments, IReadOnlyList<string> roots, bool allowMissingDependencies, bool closeAfterLaunch, WorkshopStartupRefreshPolicy startupRefresh, ThemePreference theme, bool allowMultipleInstances, bool checkForUpdates, UpdateChannelPreference updateChannel, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        var result = await bootstrapper.SavePreferencesAsync(Settings, arguments, roots, allowMissingDependencies, closeAfterLaunch, startupRefresh, theme, allowMultipleInstances, checkForUpdates, updateChannel, cancellationToken);
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
        return await RefreshModsAsync(cancellationToken);
    }

    public void DiscardModDrafts()
    {
        ResetDraftsFromSettings();
        ProjectMods(modSearchText, groupModsByCategory);
        Status = "Discarded unsaved activation and order edits";
    }

    public async Task<Result> DetectSteamAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        Status = "Detecting Steam installation";
        var result = await steamSettings.DiscoverAndApplyAsync(Settings, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value!.Settings;
        Status = $"Detected {Settings.GameInstallationLocation}";
        return await RefreshModsAsync(cancellationToken);
    }

    public async Task<Result> RefreshModsAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        Status = "Discovering mods";
        var result = await catalog.DiscoverAsync(Settings.ModRootLocations, null, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; diagnostics.Write(LocalLogLevel.Warning, "mods.discovery_failed", result.Error.Message, new Dictionary<string, string> { ["code"] = result.Error.Code }); return Result.Failure(result.Error); }
        discoveredMods = result.Value!;
        var discoveredKeys = discoveredMods.Select(mod => mod.Key).ToHashSet();
        foreach (var key in workshopStates.Keys.Where(key => !discoveredKeys.Contains(key)).ToArray()) workshopStates.Remove(key);
        foreach (var key in workshopErrors.Keys.Where(key => !discoveredKeys.Contains(key)).ToArray()) workshopErrors.Remove(key);
        foreach (var key in modDrafts.Keys.Where(key => !discoveredKeys.Contains(key)).ToArray()) modDrafts.Remove(key);
        duplicateReport = duplicateAnalyzer.Analyze(discoveredMods, Settings.DuplicatePreferences ?? []);
        var intents = Settings.ModIntents.ToDictionary(intent => intent.Mod);
        foreach (var mod in discoveredMods)
        {
            if (modDrafts.ContainsKey(mod.Key)) continue;
            intents.TryGetValue(mod.Key, out var intent);
            modDrafts[mod.Key] = new ModIntentEdit(mod.Key, intent?.IsActive ?? false, intent?.ExplicitOrder);
        }
        await RefreshDependencyStatusesAsync(cancellationToken);
        var conflictResult = await conflictService.AnalyzeAsync(discoveredMods, ActiveModKeys(), cancellationToken);
        if (!conflictResult.IsSuccess) { Status = conflictResult.Error!.Message; return Result.Failure(conflictResult.Error); }
        ApplyConflicts(conflictResult.Value!);
        var configurations = await RefreshConfigurationDocumentsAsync(cancellationToken);
        if (!configurations.IsSuccess) return configurations;
        ProjectMods(modSearchText, groupModsByCategory);
        Status = $"Discovered {discoveredMods.Count:N0} mods";
        diagnostics.Write(LocalLogLevel.Information, "mods.discovery_completed", "Mod discovery completed.", new Dictionary<string, string> { ["count"] = discoveredMods.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        return Result.Success();
    }

    public void ProjectMods(string searchText, bool groupByCategory)
    {
        if (Settings is null) return;
        modSearchText = searchText ?? string.Empty;
        groupModsByCategory = groupByCategory;
        var intents = Settings.ModIntents.ToDictionary(intent => intent.Mod);
        var items = discoveredMods.Where(mod => focusedModKeys.Count == 0 || focusedModKeys.Contains(mod.Key)).Select(mod =>
        {
            intents.TryGetValue(mod.Key, out var intent);
            var draft = modDrafts[mod.Key];
            return new ModGridItem(
                mod.Key, mod.PackageId, mod.WorkshopId, intent?.ManualName ?? mod.Name,
                draft.IsActive, intent?.IsHidden ?? false, draft.ExplicitOrder,
                intent?.Category, intent?.Tags ?? new HashSet<TagId>(),
                new ModStatus(InstallationStatus.Installed, duplicateReport.Status(mod.Key), dependencyStatuses.GetValueOrDefault(mod.Key, DependencyStatus.Unknown), conflictingMods.Contains(mod.Key) ? ConflictStatus.Conflicting : ConflictStatus.None, workshopStates.GetValueOrDefault(mod.Key)?.Update ?? UpdateStatus.Unknown),
                mod.RequiresWarOfTheChosen, mod.DateAdded);
        }).ToArray();
        var rows = ModGridProjector.Project(items, new ModGridLookups(Settings.Categories, Settings.Tags), ModGridQuery.Default with
        {
            SearchText = modSearchText,
            IncludeHidden = includeHidden,
            StateFilters = modStateFilter is { } filter ? new HashSet<ModGridSemanticState> { filter } : new HashSet<ModGridSemanticState>(),
            Grouping = groupModsByCategory ? ModGridGrouping.Category : ModGridGrouping.None,
            Sort = new ModGridSort(ModGridSortColumn.Order, SortDirection.Ascending)
        }, collapsedModGroups);
        ModRows.Clear();
        foreach (var row in rows)
        {
            if (row is ModGridGroupRow group) ModRows.Add(SessionModRow.Group(group.Key, group.Label, group.ItemCount, group.IsExpanded));
            else if (row is ModGridModRow mod) ModRows.Add(SessionModRow.Mod(mod.Item, discoveredMods.Single(item => item.Key == mod.Item.Key), mod.SemanticState.ToString(), workshopStates.GetValueOrDefault(mod.Item.Key), workshopErrors.GetValueOrDefault(mod.Item.Key), UpdateDraft));
        }
        foreach (var retained in (Settings.RetainedWorkshopItems ?? []).Where(item => discoveredMods.All(mod => mod.WorkshopId != item.WorkshopId)))
            ModRows.Add(SessionModRow.Retained(retained, retainedWorkshopStatuses.GetValueOrDefault(retained.WorkshopId)));
    }

    public void SetModGridFilter(bool showHidden, ModGridSemanticState? state)
    {
        includeHidden = showHidden;
        modStateFilter = state;
        ProjectMods(modSearchText, groupModsByCategory);
    }

    public void ToggleModGroup(ModGridGroupKey key)
    {
        if (!collapsedModGroups.Add(key)) collapsedModGroups.Remove(key);
        ProjectMods(modSearchText, groupModsByCategory);
    }

    public async Task<Result> SaveModGridPreferencesAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        var result = await bootstrapper.SaveModGridPreferencesAsync(Settings, new ModGridPreferences(includeHidden, modStateFilter, groupModsByCategory, collapsedModGroups.ToHashSet()), cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value; Status = "Mod view saved"; return Result.Success();
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
        RaiseDraftStateChanged();
        var skipped = missing.Count + duplicates.Count;
        if (changed == 0)
        {
            var reason = skipped > 0 ? BulkSkipMessage(missing, duplicates) : active ? "All selected mods are already active." : "All selected mods are already inactive.";
            return BulkActivationFailure("mods.activation_no_changes", reason);
        }
        Status = $"{(active ? "Activated" : "Deactivated")} {changed:N0} selected mod{(changed == 1 ? string.Empty : "s")}" + (skipped > 0 ? $"; {BulkSkipMessage(missing, duplicates)}" : string.Empty);
        return Result<int>.Success(changed);
    }

    public void MoveSelected(IReadOnlySet<ModKey> keys, int delta)
    {
        var ordered = discoveredMods.Select(mod => mod.Key).OrderBy(key => modDrafts.GetValueOrDefault(key)?.ExplicitOrder ?? int.MaxValue).ThenBy(key => key.LocationIdentity, StringComparer.Ordinal).ToList();
        var selected = ordered.Where(keys.Contains).ToArray();
        foreach (var key in delta < 0 ? selected : selected.Reverse())
        {
            var index = ordered.IndexOf(key); var target = Math.Clamp(index + delta, 0, ordered.Count - 1);
            ordered.RemoveAt(index); ordered.Insert(target, key);
        }
        for (var index = 0; index < ordered.Count; index++) if (modDrafts.TryGetValue(ordered[index], out var edit)) modDrafts[ordered[index]] = edit with { ExplicitOrder = index };
        ProjectMods(modSearchText, groupModsByCategory);
        RaiseDraftStateChanged();
    }

    public void RenumberMods() => MoveSelected(new HashSet<ModKey>(), 0);

    public async Task<Result> SaveModDraftsAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        Status = "Saving mod activation and load order";
        var validation = ModDuplicateActivationPolicy.Validate(discoveredMods, modDrafts.Values, duplicateReport);
        if (!validation.IsSuccess) { Status = validation.Error!.Message; return validation; }
        var result = await modIntents.SaveAsync(Settings, modDrafts.Values.ToArray(), cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        var updated = result.Value!;
        Settings = updated;
        RaiseDraftStateChanged();
        await RefreshDependencyStatusesAsync(cancellationToken);
        var conflictResult = await conflictService.SetActiveAsync(ActiveModKeys(), cancellationToken);
        if (!conflictResult.IsSuccess) { Status = conflictResult.Error!.Message; return Result.Failure(conflictResult.Error); }
        ApplyConflicts(conflictResult.Value!);
        Status = $"Saved {updated.ModIntents.Count(intent => intent.IsActive):N0} active mods";
        ProjectMods(modSearchText, groupModsByCategory);
        return Result.Success();
    }

    public async Task<Result> PreferDuplicateAsync(ModKey mod, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
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
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
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
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
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
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
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
        var conflictResult = await conflictService.SetActiveAsync(ActiveModKeys(), cancellationToken);
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
            return $"Line {entry.LineNumber}: resolution = {(matches.Length == 1 ? "installed" : matches.Length == 0 ? entry.WorkshopId.HasValue ? "missing Workshop item" : "missing local item" : "ambiguous duplicate")}";
        });
        return Result<LegacyProfilePreview>.Success(preview.Value with { Report = preview.Value.Report + "\nResolution:\n" + string.Join('\n', resolutions) });
    }

    public async Task<Result> ImportLegacyProfileAsync(string name, LegacyProfilePreview preview, LegacyTaxonomyDisposition taxonomy, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        var imported = await legacyProfileImport.ImportAsync(name, preview, taxonomy, Settings, discoveredMods, cancellationToken).ConfigureAwait(false);
        if (!imported.IsSuccess) { Status = imported.Error!.Message; return Result.Failure(imported.Error); }
        if (taxonomy == LegacyTaxonomyDisposition.AdoptIntoApplication)
        {
            var currentSettings = Settings;
            foreach (var entry in preview.Entries)
            {
                var matches = discoveredMods.Where(mod => entry.WorkshopId.HasValue ? mod.WorkshopId?.Value == entry.WorkshopId.Value : mod.PackageId.Value.Equals(entry.PackageId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) continue;
                var adopted = await metadataService.AdoptDescriptorTaxonomyAsync(currentSettings, matches[0].Key, entry.Category, entry.Tags ?? [], cancellationToken).ConfigureAwait(false);
                if (!adopted.IsSuccess) return Result.Failure(adopted.Error!);
                currentSettings = adopted.Value!;
            }
            Settings = currentSettings;
        }
        await RefreshProfilesAsync(cancellationToken).ConfigureAwait(false);
        Status = imported.Value!.Imported ? $"Imported legacy profile '{imported.Value.Profile.Name}'" : $"Legacy profile '{imported.Value.Profile.Name}' was already imported";
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<SessionMissingProfileItem>>> GetMissingProfileItemsAsync(ProfileId profileId, CancellationToken cancellationToken)
    {
        var profiles = await profileService.ListAsync(cancellationToken); if (!profiles.IsSuccess) return Result<IReadOnlyList<SessionMissingProfileItem>>.Failure(profiles.Error!);
        var profile = profiles.Value!.SingleOrDefault(item => item.Id == profileId); if (profile is null) return Result<IReadOnlyList<SessionMissingProfileItem>>.Failure(new Error("profile.not_found", "Profile was not found.", ErrorKind.NotFound));
        return Result<IReadOnlyList<SessionMissingProfileItem>>.Success(profile.Mods.Where(entry => entry.WorkshopId.HasValue && discoveredMods.All(mod => mod.WorkshopId != entry.WorkshopId)).Select(entry => new SessionMissingProfileItem(entry.WorkshopId!.Value, entry.PackageId.Value)).ToArray());
    }

    public async Task<Result> SubscribeProfileItemsAsync(IReadOnlyCollection<WorkshopId> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return Result.Failure(new Error("workshop.selection_empty", "The profile has no missing Workshop items.", ErrorKind.Validation));
        var result = await subscriptions.SubscribeAsync(ids, cancellationToken); await RefreshModsAsync(CancellationToken.None);
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
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        var result = await metadataService.SaveAsync(Settings, metadata.Key,
            new ModMetadata(metadata.ManualName, metadata.Note, metadata.IsHidden, metadata.Category, metadata.Tags), cancellationToken);
        return ApplyMetadataResult(result, "Mod metadata saved");
    }

    public async Task<Result> AssignCategoryAsync(IReadOnlyCollection<ModKey> mods, CategoryId? category, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        return ApplyMetadataResult(await metadataService.AssignCategoryAsync(Settings, mods, category, cancellationToken), $"Updated category for {mods.Count:N0} mods");
    }

    public async Task<Result> AddTagsAsync(IReadOnlyCollection<ModKey> mods, IReadOnlySet<TagId> tags, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        return ApplyMetadataResult(await metadataService.AddTagsAsync(Settings, mods, tags, cancellationToken), $"Updated tags for {mods.Count:N0} mods");
    }

    public async Task<Result> RemoveTagsAsync(IReadOnlyCollection<ModKey> mods, IReadOnlySet<TagId> tags, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        return ApplyMetadataResult(await metadataService.RemoveTagsAsync(Settings, mods, tags, cancellationToken), $"Removed tags from {mods.Count:N0} mods");
    }

    public async Task<Result> SetHiddenAsync(IReadOnlyCollection<ModKey> mods, bool hidden, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        return ApplyMetadataResult(await metadataService.SetHiddenAsync(Settings, mods, hidden, cancellationToken), hidden ? $"Hidden {mods.Count:N0} mods" : $"Unhidden {mods.Count:N0} mods");
    }

    public async Task<Result> SetTagColorAsync(TagId id, string? color, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        return ApplyMetadataResult(await metadataService.SetTagColorAsync(Settings, id, color, cancellationToken), "Tag color saved");
    }

    public async Task<Result> AdoptDescriptorTaxonomyAsync(ModKey mod, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
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
        if (Settings is null) return Result<GameLaunchOutcome>.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        diagnostics.Write(LocalLogLevel.Information, "game.launch_requested", "Game launch requested.", new Dictionary<string, string> { ["game"] = Settings.SelectedGame.ToString() });
        var duplicateValidation = ModDuplicateActivationPolicy.Validate(discoveredMods, modDrafts.Values, duplicateAnalyzer.Analyze(discoveredMods, Settings.DuplicatePreferences ?? []));
        if (!duplicateValidation.IsSuccess) { Status = duplicateValidation.Error!.Message; return Result<GameLaunchOutcome>.Failure(duplicateValidation.Error); }
        var savedIntents = await modIntents.SaveAsync(Settings, modDrafts.Values.ToArray(), cancellationToken);
        if (!savedIntents.IsSuccess)
        {
            Status = savedIntents.Error!.Message;
            return Result<GameLaunchOutcome>.Failure(savedIntents.Error);
        }
        var settings = savedIntents.Value!;
        Settings = settings;
        ResetDraftsFromSettings();
        if (string.IsNullOrWhiteSpace(settings.GameInstallationLocation))
        {
            Status = "Configure the selected game installation before launching.";
            return Result<GameLaunchOutcome>.Failure(new Error("launch.installation_required", Status, ErrorKind.Validation));
        }
        var installations = discoveredMods.ToDictionary(mod => mod.Key);
        var active = settings.ModIntents
            .Where(intent => intent.IsActive && installations.ContainsKey(intent.Mod))
            .OrderBy(intent => intent.ExplicitOrder ?? int.MaxValue)
            .ThenBy(intent => installations[intent.Mod].PackageId.Value, StringComparer.OrdinalIgnoreCase)
            .Select((intent, order) => new GameLaunchMod(intent.Mod, installations[intent.Mod].PackageId, order, installations[intent.Mod].RequiresWarOfTheChosen))
            .ToArray();
        var activeInstallations = active.Select(mod => installations[mod.Mod]).ToArray();
        var activeWorkshop = activeInstallations.Where(mod => mod.WorkshopId.HasValue).Select(mod => mod.WorkshopId!.Value).ToHashSet();
        var installedWorkshop = discoveredMods.Where(mod => mod.WorkshopId.HasValue).Select(mod => mod.WorkshopId!.Value).ToHashSet();
        var ignored = settings.ModIntents.Where(intent => intent.IsActive && installations.TryGetValue(intent.Mod, out var mod) && mod.WorkshopId.HasValue)
            .ToDictionary(intent => installations[intent.Mod].WorkshopId!.Value, intent => intent.IgnoredDependencies);
        var dependencyResult = await dependencies.EvaluateAsync(activeWorkshop, installedWorkshop, activeWorkshop, ignored, cancellationToken);
        if (!dependencyResult.IsSuccess) { Status = dependencyResult.Error!.Message; return Result<GameLaunchOutcome>.Failure(dependencyResult.Error); }
        if (dependencyResult.Value!.HasBlockingIssues && !settings.AllowLaunchWithMissingDependencies)
        {
            Status = string.Join("; ", dependencyResult.Value.Issues.Where(issue => issue.Kind is ModDependencyIssueKind.Missing or ModDependencyIssueKind.Inactive or ModDependencyIssueKind.MetadataUnavailable).Select(issue => issue.Message));
            return Result<GameLaunchOutcome>.Failure(new Error("launch.dependencies_blocked", Status, ErrorKind.Conflict));
        }
        Status = $"Launching {settings.SelectedGame}";
        var request = new GameLaunchRequest(settings.SelectedGame, settings.GameInstallationLocation, settings.ModRootLocations, active, settings.LaunchArguments);
        var result = await launchCoordinator.LaunchAsync(request, cancellationToken);
        Status = result.IsSuccess
            ? $"Started {settings.SelectedGame} with {result.Value!.Configuration?.ActivePackageIds.Count ?? 0:N0} mods (process {result.Value.Launch.ProcessId})"
            : result.Error!.Message;
        diagnostics.Write(result.IsSuccess ? LocalLogLevel.Information : LocalLogLevel.Error, result.IsSuccess ? "game.launch_completed" : "game.launch_failed", Status, result.IsSuccess ? null : new Dictionary<string, string> { ["code"] = result.Error!.Code });
        await diagnostics.FlushAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<Result> ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        var duplicateValidation = ModDuplicateActivationPolicy.Validate(discoveredMods, modDrafts.Values, duplicateAnalyzer.Analyze(discoveredMods, Settings.DuplicatePreferences ?? []));
        if (!duplicateValidation.IsSuccess) { Status = duplicateValidation.Error!.Message; return duplicateValidation; }
        var saved = await modIntents.SaveAsync(Settings, modDrafts.Values.ToArray(), cancellationToken);
        if (!saved.IsSuccess) { Status = saved.Error!.Message; return Result.Failure(saved.Error); }
        var current = saved.Value!;
        Settings = current;
        ResetDraftsFromSettings();
        if (string.IsNullOrWhiteSpace(current.GameInstallationLocation)) return Result.Failure(new Error("configuration.installation_required", "Configure the selected game installation first.", ErrorKind.Validation));
        var installations = discoveredMods.ToDictionary(mod => mod.Key);
        var active = current.ModIntents.Where(intent => intent.IsActive && installations.ContainsKey(intent.Mod)).OrderBy(intent => intent.ExplicitOrder ?? int.MaxValue).ThenBy(intent => installations[intent.Mod].PackageId.Value, StringComparer.OrdinalIgnoreCase).Select((intent, order) => new GameLaunchMod(intent.Mod, installations[intent.Mod].PackageId, order, installations[intent.Mod].RequiresWarOfTheChosen)).ToArray();
        var receipt = await configurationWriter.ApplyAsync(new GameLaunchRequest(current.SelectedGame, current.GameInstallationLocation, current.ModRootLocations, active, current.LaunchArguments), cancellationToken);
        Status = receipt.IsSuccess ? $"Applied configuration with {receipt.Value!.ActivePackageIds.Count:N0} mods" : receipt.Error!.Message;
        return receipt.IsSuccess ? Result.Success() : Result.Failure(receipt.Error!);
    }

    private void UpdateDraft(ModKey key, bool isActive, int? order)
    {
        modDrafts[key] = new ModIntentEdit(key, isActive, order);
        RaiseDraftStateChanged();
        Status = HasUnsavedModDrafts ? $"{UnsavedModDraftCount:N0} unsaved activation/order edit{(UnsavedModDraftCount == 1 ? string.Empty : "s")}" : "Activation and load order match saved settings";
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
        foreach (var mod in discoveredMods)
        {
            intents.TryGetValue(mod.Key, out var intent);
            modDrafts[mod.Key] = new ModIntentEdit(mod.Key, intent?.IsActive ?? false, intent?.ExplicitOrder);
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
        this.RaisePropertyChanged(nameof(UnsavedModDraftCount));
        this.RaisePropertyChanged(nameof(HasUnsavedModDrafts));
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
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        var installation = discoveredMods.SingleOrDefault(item => item.Key == mod);
        if (installation?.WorkshopId is not { } id) return Result.Failure(new Error("workshop.identity_missing", "The selected mod has no Workshop identity.", ErrorKind.Validation));
        var item = await workshopService.GetItemAsync(id, cancellationToken);
        if (!item.IsSuccess || item.Value is null) return Result.Failure(item.Error ?? new Error("workshop.item_missing", "Steam returned no Workshop item.", ErrorKind.NotFound));
        return ApplyMetadataResult(await metadataService.AdoptDescriptorTaxonomyAsync(Settings, mod, null, item.Value.Tags ?? [], cancellationToken), "Workshop tags adopted");
    }

    public async Task<Result<SessionDependencyDetails>> LoadDependencyDetailsAsync(ModKey mod, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result<SessionDependencyDetails>.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
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
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
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
        return new SessionDependencyRelationship(parent, related, installation?.Name ?? $"Workshop {related.Value}", installation?.Key, ignored ? "Ignored" : issue?.Kind.ToString() ?? (installation is null ? "Missing" : "Satisfied"), ignored, depth, path);
    }

    private static IEnumerable<(WorkshopId Parent, WorkshopId Related, int Depth, IReadOnlyList<WorkshopId> Path)> TraverseRequired(WorkshopId root, IReadOnlyDictionary<WorkshopId, IReadOnlyList<WorkshopId>> graph)
    {
        var queue = new Queue<(WorkshopId Node, IReadOnlyList<WorkshopId> Path)>(); queue.Enqueue((root, new[] { root }));
        var seen = new HashSet<(WorkshopId, WorkshopId)>();
        while (queue.Count > 0) { var current = queue.Dequeue(); foreach (var child in graph.GetValueOrDefault(current.Node) ?? []) { if (!seen.Add((current.Node, child))) continue; var path = current.Path.Append(child).ToArray(); yield return (current.Node, child, path.Length - 1, path); if (!current.Path.Contains(child)) queue.Enqueue((child, path)); } }
    }

    private static IReadOnlyList<WorkshopId>? FindPath(WorkshopId root, WorkshopId target, IReadOnlyDictionary<WorkshopId, IReadOnlyList<WorkshopId>> graph)
    {
        var queue = new Queue<IReadOnlyList<WorkshopId>>(); queue.Enqueue(new[] { root }); var seen = new HashSet<WorkshopId> { root };
        while (queue.Count > 0) { var path = queue.Dequeue(); foreach (var child in graph.GetValueOrDefault(path[^1]) ?? []) { var next = path.Append(child).ToArray(); if (child == target) return next; if (seen.Add(child)) queue.Enqueue(next); } } return null;
    }

    public async Task<Result> RefreshWorkshopStatesAsync(IProgress<WorkshopOperationProgress>? progress, CancellationToken cancellationToken)
    {
        if (!await TryEnterWorkshopAsync(cancellationToken)) return WorkshopBusy();
        SetWorkshopAvailability(WorkshopConnectionState.Connecting);
        var forwarding = new Progress<WorkshopOperationProgress>(update =>
        {
            if (update.State is not null) ApplyWorkshopProgress(update.State);
            progress?.Report(update);
        });
        WorkshopBatchResult result;
        try { result = await workshopOperations.RefreshAsync(discoveredMods, forwarding, cancellationToken); }
        finally { ExitWorkshop(); }
        ApplyWorkshopOutcomes(result.Items);
        ProjectMods(modSearchText, groupModsByCategory);
        Status = DescribeWorkshopResult("Workshop state refreshed", result);
        ObserveWorkshopAvailability(result);
        return result.IsSuccess ? Result.Success() : Result.Failure(new Error("workshop.refresh_partial", Status, result.IsPartialSuccess ? ErrorKind.ExternalService : result.Items.FirstOrDefault(item => !item.Outcome.IsSuccess)?.Outcome.Error?.Kind ?? ErrorKind.ExternalService));
    }

    public async Task<Result> DownloadWorkshopUpdatesAsync(IReadOnlySet<ModKey> mods, IProgress<WorkshopOperationProgress>? progress, CancellationToken cancellationToken)
    {
        var selected = discoveredMods.Where(mod => mods.Contains(mod.Key) && mod.WorkshopId.HasValue).ToArray();
        if (selected.Length == 0) return Result.Failure(new Error("workshop.selection_empty", "Select at least one Workshop mod.", ErrorKind.Validation));
        if (!await TryEnterWorkshopAsync(cancellationToken)) return WorkshopBusy();
        SetWorkshopAvailability(WorkshopConnectionState.Connecting);
        double aggregate = 0;
        var forwarding = new Progress<WorkshopOperationProgress>(update =>
        {
            if (update.State is not null) ApplyWorkshopProgress(update.State);
            if (update.BytesTotal is > 0) aggregate = Math.Max(aggregate, (double)update.BytesDownloaded / update.BytesTotal.Value);
            Status = update.BytesTotal is > 0 ? $"Downloading Workshop content: {aggregate:P0}" : $"Monitoring Workshop downloads: {update.CompletedItems:N0}/{update.TotalItems:N0}";
            progress?.Report(update);
        });
        WorkshopBatchResult result;
        try { result = await workshopOperations.DownloadUpdatesAsync(selected, WorkshopDownloadOptions.Default, forwarding, cancellationToken); }
        finally { ExitWorkshop(); }
        ApplyWorkshopOutcomes(result.Items);
        await RefreshModsAsync(CancellationToken.None);
        ApplyWorkshopOutcomes(result.Items);
        ProjectMods(modSearchText, groupModsByCategory);
        Status = DescribeWorkshopResult(result.ObservationCancelled ? "Stopped monitoring Workshop downloads; Steam may continue" : "Workshop downloads completed", result);
        ObserveWorkshopAvailability(result);
        return result.IsSuccess ? Result.Success() : Result.Failure(new Error("workshop.download_partial", Status, result.ObservationCancelled ? ErrorKind.Cancelled : ErrorKind.ExternalService));
    }

    public async Task<Result> SubscribeRetainedAsync(WorkshopId id, CancellationToken cancellationToken)
    {
        if (!await TryEnterWorkshopAsync(cancellationToken)) return WorkshopBusy();
        WorkshopMutationResult result;
        try { result = await subscriptions.SubscribeAsync([id], cancellationToken); }
        finally { ExitWorkshop(); }
        await RefreshModsAsync(CancellationToken.None);
        await RefreshProfilesAsync(CancellationToken.None);
        var item = result.Items.Single();
        retainedWorkshopStatuses[id] = item.Subscribed
            ? item.DownloadRequested ? "Subscribed; waiting for Steam download" : $"Subscribed; download request failed: {item.DownloadRequestOutcome!.Value.Error!.Message}"
            : $"Subscription failed: {item.Outcome.Error!.Message}";
        ProjectMods(modSearchText, groupModsByCategory);
        Status = item.Subscribed && !item.DownloadRequested
            ? $"Subscribed to Workshop item {id.Value}, but the download request failed: {item.DownloadRequestOutcome!.Value.Error!.Message}"
            : result.IsSuccess ? $"Subscribed to Workshop item {id.Value}; waiting for Steam download" : item.Outcome.Error!.Message;
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Items.Single().Outcome.Error!);
    }

    public async Task<Result> UnsubscribeRetainingIntentAsync(IReadOnlySet<ModKey> mods, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        if (!await TryEnterWorkshopAsync(cancellationToken)) return WorkshopBusy();
        Result<(ApplicationSettings Settings, WorkshopMutationResult Mutations)> result;
        try { result = await subscriptions.UnsubscribeRetainingIntentAsync(Settings, discoveredMods, mods, cancellationToken); }
        finally { ExitWorkshop(); }
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        Settings = result.Value!.Settings;
        await RefreshModsAsync(CancellationToken.None);
        await RefreshProfilesAsync(CancellationToken.None);
        Status = result.Value.Mutations.IsSuccess ? "Unsubscribed and retained mod intent" : "Some Workshop unsubscribe operations failed";
        return result.Value.Mutations.IsSuccess ? Result.Success() : Result.Failure(new Error("workshop.unsubscribe_partial", Status, ErrorKind.ExternalService));
    }

    public async Task<Result> RemoveRetainedIntentAsync(WorkshopId id, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        var result = await subscriptions.RemoveRetainedIntentAsync(Settings, id, cancellationToken);
        if (!result.IsSuccess) return Result.Failure(result.Error!);
        Settings = result.Value; ProjectMods(modSearchText, groupModsByCategory); Status = "Removed retained Workshop intent"; return Result.Success();
    }

    public Task<Result<ModRemovalPreview>> PreviewManualRemovalAsync(ModKey mod, CancellationToken cancellationToken) => removalFilesystem.PreviewAsync(mod, Settings?.ModRootLocations ?? [], cancellationToken);

    public async Task<Result> ConfirmManualRemovalAsync(ModRemovalPreview preview, CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        var deleted = await removalFilesystem.DeleteConfirmedAsync(preview, cancellationToken); if (!deleted.IsSuccess) return deleted;
        var removed = await modIntents.RemoveAsync(Settings, preview.Mod, cancellationToken); if (!removed.IsSuccess) return Result.Failure(removed.Error!);
        Settings = removed.Value; await RefreshModsAsync(CancellationToken.None); await RefreshProfilesAsync(CancellationToken.None); Status = "Manual mod removed"; return Result.Success();
    }

    public async Task<Result> RefreshConfigurationDocumentsAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return Result.Failure(new Error("session.not_initialized", "AAML is not initialized.", ErrorKind.Unavailable));
        var result = await configurationCatalog.ListAsync(discoveredMods, Settings.SelectedGame, cancellationToken);
        if (!result.IsSuccess) { Status = result.Error!.Message; return Result.Failure(result.Error); }
        ConfigurationDocuments.Clear();
        foreach (var document in result.Value!) ConfigurationDocuments.Add(document);
        return Result.Success();
    }

    private IReadOnlySet<ModKey> ActiveModKeys() => Settings?.ModIntents.Where(intent => intent.IsActive).Select(intent => intent.Mod).ToHashSet() ?? new HashSet<ModKey>();

    private void ApplyConflicts(ModConflictReport report)
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
        foreach (var outcome in outcomes)
        {
            if (outcome.State is not null) workshopStates[outcome.Mod] = outcome.State;
            if (outcome.Outcome.IsSuccess) workshopErrors.Remove(outcome.Mod);
            else workshopErrors[outcome.Mod] = outcome.Outcome.Error!.Message;
        }
    }

    private async Task ApplyStartupWorkshopPolicyAsync(CancellationToken cancellationToken)
    {
        if (Settings is null) return;
        IReadOnlyList<ModInstallation> selected = Settings.WorkshopStartupRefresh is WorkshopStartupRefreshPolicy.AllMods or WorkshopStartupRefreshPolicy.Manual
            ? discoveredMods.Where(mod => mod.WorkshopId.HasValue).ToArray()
            : discoveredMods.Where(mod => mod.WorkshopId.HasValue && Settings.ModIntents.Any(intent => intent.Mod == mod.Key && intent.IsActive)).ToArray();
        if (selected.Count == 0) return;
        if (!await TryEnterWorkshopAsync(cancellationToken)) return;
        SetWorkshopAvailability(WorkshopConnectionState.Connecting);
        WorkshopBatchResult result;
        try { result = await workshopOperations.RefreshAsync(selected, null, cancellationToken); }
        finally { ExitWorkshop(); }
        ApplyWorkshopOutcomes(result.Items);
        ProjectMods(modSearchText, groupModsByCategory);
        Status = DescribeWorkshopResult("Workshop state checked during startup", result);
        ObserveWorkshopAvailability(result);
    }

    private void ApplyWorkshopProgress(WorkshopModState state)
    {
        foreach (var mod in discoveredMods.Where(mod => mod.WorkshopId == state.WorkshopId))
        {
            workshopStates[mod.Key] = state with { Mod = mod.Key };
            workshopErrors.Remove(mod.Key);
        }
        foreach (var row in ModRows.Where(row => row.Key is not null && workshopStates.TryGetValue(row.Key.Value, out _)))
            row.ApplyWorkshop(workshopStates[row.Key!.Value], workshopErrors.GetValueOrDefault(row.Key.Value));
    }

    private async Task<bool> TryEnterWorkshopAsync(CancellationToken cancellationToken)
    {
        var entered = await workshopGate.WaitAsync(0, cancellationToken);
        if (entered) this.RaisePropertyChanged(nameof(IsWorkshopBusy));
        return entered;
    }

    private void ExitWorkshop()
    {
        workshopGate.Release();
        this.RaisePropertyChanged(nameof(IsWorkshopBusy));
    }

    private static Result WorkshopBusy() => Result.Failure(new Error("workshop.operation_in_progress", "Another Workshop operation is already in progress.", ErrorKind.Conflict));

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
        dependencyStatuses.Clear();
        if (Settings is null) return;
        var installations = discoveredMods.ToDictionary(mod => mod.Key);
        var activeIntents = Settings.ModIntents.Where(intent => intent.IsActive && installations.ContainsKey(intent.Mod)).ToArray();
        var activeWorkshopMods = activeIntents.Select(intent => installations[intent.Mod]).Where(mod => mod.WorkshopId.HasValue).ToArray();
        var activeIds = activeWorkshopMods.Select(mod => mod.WorkshopId!.Value).ToHashSet();
        if (activeIds.Count == 0) return;
        var installedIds = discoveredMods.Where(mod => mod.WorkshopId.HasValue).Select(mod => mod.WorkshopId!.Value).ToHashSet();
        var ignored = activeIntents.Where(intent => installations[intent.Mod].WorkshopId.HasValue)
            .ToDictionary(intent => installations[intent.Mod].WorkshopId!.Value, intent => intent.IgnoredDependencies);
        var result = await dependencies.EvaluateAsync(activeIds, installedIds, activeIds, ignored, cancellationToken);
        if (!result.IsSuccess) return;
        foreach (var mod in activeWorkshopMods)
        {
            var id = mod.WorkshopId!.Value;
            var related = result.Value!.Issues.Where(issue => issue.Path.Count > 0 && issue.Path[0] == id).ToArray();
            dependencyStatuses[mod.Key] = related.Any(issue => issue.Kind is ModDependencyIssueKind.Missing or ModDependencyIssueKind.Inactive)
                ? DependencyStatus.Missing
                : related.Any(issue => issue.Kind == ModDependencyIssueKind.MetadataUnavailable)
                    ? DependencyStatus.Unknown
                    : DependencyStatus.Satisfied;
        }
    }
}

public sealed record SessionProfile(ProfileId Id, string Name, GameVariant GameVariant, int ModCount);
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

    private SessionModRow(ModKey? key, ModGridGroupKey? groupKey, string name, bool? active, int? explicitOrder, string state, int? count, bool isExpanded, bool requiresWotc, PackageId? packageId, WorkshopId? workshopId, DuplicateStatus duplicateStatus, WorkshopModState? workshopState, string? workshopError, Action<ModKey, bool, int?>? update)
    {
        Key = key;
        GroupKey = groupKey;
        Name = name;
        isActive = active;
        order = explicitOrder;
        State = state;
        Count = count;
        IsExpanded = isExpanded;
        RequiresWarOfTheChosen = requiresWotc;
        PackageId = packageId;
        WorkshopId = workshopId;
        DuplicateStatus = duplicateStatus;
        this.update = update;
        ApplyWorkshop(workshopState, workshopError);
    }

    public ModKey? Key { get; }
    public ModGridGroupKey? GroupKey { get; }
    public bool IsGroup => Key is null;
    public string Name { get; }
    public string State { get; }
    public int? Count { get; }
    public bool IsExpanded { get; }
    public bool RequiresWarOfTheChosen { get; }
    public PackageId? PackageId { get; }
    public WorkshopId? WorkshopId { get; }
    public DuplicateStatus DuplicateStatus { get; }
    public bool CanActivate => !IsGroup && State != "Missing" && DuplicateStatus is DuplicateStatus.None or DuplicateStatus.Preferred;
    public bool IsRetainedMissing => State == "Missing" && WorkshopId.HasValue;
    public string Source => Key?.Source.ToString() ?? string.Empty;
    public string Location => Key?.LocationIdentity ?? string.Empty;
    public string Description { get; private init; } = string.Empty;
    public string DescriptorTags { get; private init; } = string.Empty;
    public string DescriptorCategory { get; private init; } = string.Empty;
    public string ReadmePath { get; private init; } = string.Empty;
    public string PreviewImagePath { get; private init; } = string.Empty;
    public string WorkshopUrl => WorkshopId is { } id ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={id.Value}" : string.Empty;
    public string Workshop { get => workshop; private set => this.RaiseAndSetIfChanged(ref workshop, value); }
    public double? DownloadProgress { get => downloadProgress; private set => this.RaiseAndSetIfChanged(ref downloadProgress, value); }
    public bool IsDownloading { get => isDownloading; private set => this.RaiseAndSetIfChanged(ref isDownloading, value); }
    public bool? IsActive
    {
        get => isActive;
        set
        {
            if (value == true && !CanActivate) return;
            this.RaiseAndSetIfChanged(ref isActive, value);
            if (Key is { } key && value.HasValue) update?.Invoke(key, value.Value, order);
        }
    }
    public int? Order
    {
        get => order;
        set
        {
            this.RaiseAndSetIfChanged(ref order, value);
            if (Key is { } key) update?.Invoke(key, isActive ?? false, value);
        }
    }

    public void ApplyWorkshop(WorkshopModState? state, string? error)
    {
        IsDownloading = state?.Update == UpdateStatus.Downloading;
        DownloadProgress = state?.Download?.Fraction is { } fraction ? fraction * 100 : null;
        Workshop = error is not null ? $"Unavailable: {error}" : state?.Update switch
        {
            UpdateStatus.Current => "Current",
            UpdateStatus.Available => "Update available",
            UpdateStatus.Downloading when state.Download?.Fraction is { } downloadFraction => $"Downloading {downloadFraction:P0}",
            UpdateStatus.Downloading => state.RawState.HasFlag(WorkshopItemState.DownloadPending) ? "Queued" : "Downloading",
            _ => state is null ? string.Empty : "Unknown"
        };
    }

    public static SessionModRow Group(ModGridGroupKey key, string name, int count, bool isExpanded) => new(null, key, name, null, null, string.Empty, count, isExpanded, false, null, null, DuplicateStatus.None, null, null, null);
    public static SessionModRow Mod(ModGridItem item, ModInstallation installation, string state, WorkshopModState? workshop, string? workshopError, Action<ModKey, bool, int?> update) => new(item.Key, null, item.DisplayName, item.IsActive, item.ExplicitOrder, state, null, false, item.RequiresWarOfTheChosen, item.PackageId, item.WorkshopId, item.Status.Duplicate, workshop, workshopError, update)
    {
        Description = installation.Metadata?.Description ?? string.Empty,
        DescriptorTags = string.Join(", ", installation.Metadata?.DescriptorTags ?? []),
        DescriptorCategory = installation.Metadata?.DescriptorCategory ?? string.Empty,
        ReadmePath = installation.Metadata?.ReadmePath ?? string.Empty,
        PreviewImagePath = installation.Metadata?.PreviewImagePath ?? string.Empty
    };
    public static SessionModRow Retained(RetainedWorkshopItem item, string? workshopStatus = null) => new(item.LastKnownKey, null, item.Name, false, null, "Missing", null, false, false, item.PackageId, item.WorkshopId, DuplicateStatus.None, null, workshopStatus ?? "Retained intent; subscription/download state unknown", null);
}
