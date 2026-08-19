using AAML.Application.Mods.Grid;
using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;

namespace AAML.Infrastructure.Common.Settings;

internal static class ApplicationSettingsMapper
{
    public static CurrentSettingsDocument ToDocument(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var locations = (settings.GameLocations ?? new Dictionary<GameVariant, GameLocationSettings>()).ToDictionary();
        locations[settings.SelectedGame] = new GameLocationSettings(
            settings.GameInstallationLocation,
            settings.ModRootLocations ?? []);
        var grid = settings.ModGrid ?? ModGridPreferences.Default;

        return new CurrentSettingsDocument(
            ApplicationSettingsDefaults.CurrentSchemaVersion,
            RequireDefined(settings.SelectedGame).ToString(),
            settings.GameInstallationLocation,
            (settings.ModRootLocations ?? []).ToArray(),
            (settings.LaunchArguments ?? []).Select(argument => argument.Value).ToArray(),
            (settings.ModIntents ?? []).Select(intent => new ModIntentDocument(
                RequireDefined(intent.Mod.Source).ToString(), intent.Mod.LocationIdentity, intent.IsActive, intent.IsHidden,
                intent.ExplicitOrder, intent.ManualName, intent.Category?.Value, (intent.Tags ?? new HashSet<TagId>()).Select(tag => tag.Value).ToArray(),
                intent.Note, (intent.IgnoredDependencies ?? new HashSet<WorkshopId>()).Select(id => id.Value).ToArray())).ToArray(),
            (settings.Categories ?? []).Select(category => new CategoryDocument(category.Id.Value, category.Name, category.Order)).ToArray(),
            (settings.Tags ?? []).Select(tag => new TagDocument(tag.Id.Value, tag.Name, tag.Color)).ToArray(),
            settings.AllowLaunchWithMissingDependencies,
            locations.OrderBy(item => item.Key).Select(item => new GameLocationDocument(RequireDefined(item.Key).ToString(), item.Value.InstallationLocation, (item.Value.ModRootLocations ?? []).ToArray())).ToArray(),
            settings.CloseAfterLaunch,
            RequireDefined(settings.WorkshopStartupRefresh).ToString(),
            RequireDefined(settings.Theme).ToString(),
            settings.AllowMultipleInstances,
            (settings.DuplicatePreferences ?? []).Select(preference => new DuplicatePreferenceDocument(preference.PackageId.Value, RequireDefined(preference.PreferredInstallation.Source).ToString(), preference.PreferredInstallation.LocationIdentity)).ToArray(),
            new ModGridDocument { IncludeHidden = grid.IncludeHidden, StateFilter = grid.StateFilter is { } state ? RequireDefined(state).ToString() : null, GroupByCategory = grid.GroupByCategory, CollapsedGroups = (grid.CollapsedGroups ?? new HashSet<ModGridGroupKey>()).Select(key => new ModGridGroupDocument(key.Grouping, key.Bucket)).ToArray() },
            (settings.RetainedWorkshopItems ?? []).Select(item => new RetainedWorkshopDocument(item.WorkshopId.Value, item.PackageId.Value, item.Name, RequireDefined(item.LastKnownKey.Source).ToString(), item.LastKnownKey.LocationIdentity)).ToArray(),
            settings.CheckForUpdates,
            RequireDefined(settings.UpdateChannel).ToString(),
            RequireDefined(settings.NavigationRailMode).ToString(),
            settings.AutoSaveChanges,
            RequireTextScale(settings.TextScale),
            RequireIconScale(settings.IconScale));
    }

    public static ApplicationSettings FromCurrentDocument(CurrentSettingsDocument document)
    {
        if (document.SchemaVersion != ApplicationSettingsDefaults.CurrentSchemaVersion)
            throw new InvalidDataException($"Expected settings schema {ApplicationSettingsDefaults.CurrentSchemaVersion}.");
        if (document.ModRootLocations is null || document.LaunchArguments is null || document.ModIntents is null ||
            document.Categories is null || document.Tags is null || document.GameLocations is null ||
            document.DuplicatePreferences is null || document.ModGrid is null || document.RetainedWorkshopItems is null ||
            document.ModGrid.IncludeHidden is null || document.ModGrid.GroupByCategory is null || document.ModGrid.CollapsedGroups is null)
        {
            throw new InvalidDataException("Schema 10 collection and modGrid members must be non-null.");
        }

        var game = ParseNamed<GameVariant>(document.SelectedGame, "selectedGame");
        var locations = ParseLocations(document.GameLocations);
        if (!locations.TryGetValue(game, out var selectedLocation) ||
            !LocationsEqual(selectedLocation, document.GameInstallationLocation, document.ModRootLocations))
        {
            throw new InvalidDataException("Schema 10 selected scalar locations contradict gameLocations.");
        }

        return Create(
            game, document.GameInstallationLocation, document.ModRootLocations, document.LaunchArguments,
            document.ModIntents, document.Categories, document.Tags, document.AllowLaunchWithMissingDependencies,
            locations, document.CloseAfterLaunch,
            ParseWorkshop(document.WorkshopStartupRefresh, compatibility: false),
            ParseNamed<ThemePreference>(document.Theme, "theme"), document.AllowMultipleInstances,
            document.DuplicatePreferences, ParseGrid(document.ModGrid), document.RetainedWorkshopItems,
            document.CheckForUpdates, ParseNamed<UpdateChannelPreference>(document.UpdateChannel, "updateChannel"),
            ParseNamed<NavigationRailMode>(document.NavigationRailMode, "navigationRailMode"), document.AutoSaveChanges,
            RequireTextScale(document.TextScale), RequireIconScale(document.IconScale));
    }

    public static ApplicationSettings FromMigratedDocument(SettingsMigrationState state)
    {
        var game = ParseNamed<GameVariant>(state.SelectedGame, "selectedGame");
        var locations = ParseLocations(state.GameLocations);
        locations[game] = new GameLocationSettings(state.GameInstallationLocation, state.ModRootLocations ?? []);
        var arguments = state.SourceSchemaVersion == 1 && (state.LaunchArguments?.Count ?? 0) == 0
            ? ApplicationSettingsDefaults.LaunchArguments.Select(argument => argument.Value).ToArray()
            : state.LaunchArguments ?? [];

        return Create(
            game, state.GameInstallationLocation, state.ModRootLocations ?? [], arguments,
            state.ModIntents ?? [], state.Categories ?? [], state.Tags ?? [], state.AllowLaunchWithMissingDependencies,
            locations, state.CloseAfterLaunch, ParseWorkshop(state.WorkshopStartupRefresh, compatibility: state.SourceSchemaVersion < 7),
            ParseOptionalNamed(state.Theme, ThemePreference.System, "theme"), state.AllowMultipleInstances,
            state.DuplicatePreferences ?? [], ParseGrid(state.ModGrid), state.RetainedWorkshopItems ?? [],
            state.CheckForUpdates ?? true, ParseOptionalNamed(state.UpdateChannel, UpdateChannelPreference.Stable, "updateChannel"),
            ParseOptionalNamed(state.NavigationRailMode, NavigationRailMode.Expanded, "navigationRailMode"), state.AutoSaveChanges,
            state.TextScale, state.IconScale);
    }

    private static ApplicationSettings Create(
        GameVariant game, string? gameLocation, IReadOnlyList<string> roots, IReadOnlyList<string> arguments,
        IReadOnlyList<ModIntentDocument> intents, IReadOnlyList<CategoryDocument> categories, IReadOnlyList<TagDocument> tags,
        bool allowMissing, IReadOnlyDictionary<GameVariant, GameLocationSettings> locations, bool closeAfter,
        WorkshopStartupRefreshPolicy workshop, ThemePreference theme, bool allowMultiple,
        IReadOnlyList<DuplicatePreferenceDocument> duplicates, ModGridPreferences grid,
        IReadOnlyList<RetainedWorkshopDocument> retained, bool checkForUpdates, UpdateChannelPreference updateChannel,
         NavigationRailMode navigationRailMode, bool autoSaveChanges, decimal textScale, decimal iconScale) => new(
            ApplicationSettingsDefaults.CurrentSchemaVersion,
            game,
            gameLocation,
            roots.ToArray(),
            arguments.Select(argument => new LaunchArgument(argument)).ToArray(),
            intents.Select(intent => new ModUserIntent(
                new ModKey(ParseNamed<ModSource>(intent.Source, "modIntents.source"), intent.LocationIdentity),
                intent.IsActive, intent.IsHidden, intent.ExplicitOrder, intent.ManualName,
                string.IsNullOrWhiteSpace(intent.CategoryId) ? null : new CategoryId(intent.CategoryId),
                (intent.TagIds ?? []).Select(id => new TagId(id)).ToHashSet(), intent.Note,
                (intent.IgnoredDependencies ?? []).Select(id => new WorkshopId(id)).ToHashSet())).ToArray(),
            categories.Select(category => new Category(new CategoryId(category.Id), category.Name, category.Order)).ToArray(),
            tags.Select(tag => new Tag(new TagId(tag.Id), tag.Name, tag.Color)).ToArray(),
            allowMissing,
            locations,
            closeAfter,
            workshop,
            theme,
            allowMultiple,
            duplicates.Select(preference => new DuplicatePreference(new PackageId(preference.PackageId), new ModKey(ParseNamed<ModSource>(preference.Source, "duplicatePreferences.source"), preference.LocationIdentity))).ToArray(),
            grid,
            retained.Select(item => new RetainedWorkshopItem(new WorkshopId(item.WorkshopId), new PackageId(item.PackageId), item.Name, new ModKey(ParseNamed<ModSource>(item.Source, "retainedWorkshopItems.source"), item.LocationIdentity))).ToArray(),
            checkForUpdates,
            updateChannel,
            navigationRailMode,
            autoSaveChanges,
            RequireTextScale(textScale),
            RequireIconScale(iconScale));

    private static decimal RequireTextScale(decimal value) => ApplicationSettingsDefaults.IsTextScaleSupported(value)
        ? value
        : throw new InvalidDataException($"textScale must be between {ApplicationSettingsDefaults.MinimumTextScale} and {ApplicationSettingsDefaults.MaximumTextScale}.");

    private static decimal RequireIconScale(decimal value) => ApplicationSettingsDefaults.IsIconScaleSupported(value)
        ? value
        : throw new InvalidDataException($"iconScale must be between {ApplicationSettingsDefaults.MinimumIconScale} and {ApplicationSettingsDefaults.MaximumIconScale}.");

    private static Dictionary<GameVariant, GameLocationSettings> ParseLocations(IEnumerable<GameLocationDocument>? documents)
    {
        var result = new Dictionary<GameVariant, GameLocationSettings>();
        foreach (var item in documents ?? [])
        {
            var game = ParseNamed<GameVariant>(item.Game, "gameLocations.game");
            if (!result.TryAdd(game, new GameLocationSettings(item.InstallationLocation, (item.ModRootLocations ?? []).ToArray())))
                throw new InvalidDataException($"gameLocations contains duplicate game '{item.Game}'.");
        }

        return result;
    }

    private static ModGridPreferences ParseGrid(ModGridDocument? document)
    {
        if (document is null) return ModGridPreferences.Default;
        ModGridSemanticState? filter = document.StateFilter is null ? null : ParseNamed<ModGridSemanticState>(document.StateFilter, "modGrid.stateFilter");
        return new ModGridPreferences(
            document.IncludeHidden ?? ModGridPreferences.Default.IncludeHidden,
            filter,
            document.GroupByCategory ?? ModGridPreferences.Default.GroupByCategory,
            (document.CollapsedGroups ?? []).Select(key => new ModGridGroupKey(key.Grouping, key.Bucket)).ToHashSet());
    }

    private static WorkshopStartupRefreshPolicy ParseWorkshop(string? value, bool compatibility)
    {
        if (compatibility && value is null) return WorkshopStartupRefreshPolicy.AllMods;
        if (compatibility && value == "Never") return WorkshopStartupRefreshPolicy.Manual;
        return value switch
        {
            "AllMods" => WorkshopStartupRefreshPolicy.AllMods,
            "ActiveMods" => WorkshopStartupRefreshPolicy.ActiveMods,
            "Manual" => WorkshopStartupRefreshPolicy.Manual,
            _ => throw new InvalidDataException($"workshopStartupRefresh has invalid named value '{value ?? "<missing>"}'.")
        };
    }

    private static T ParseOptionalNamed<T>(string? value, T defaultValue, string field) where T : struct, Enum =>
        value is null ? defaultValue : ParseNamed<T>(value, field);

    private static T ParseNamed<T>(string? value, string field) where T : struct, Enum
    {
        if (value is null || value.Length == 0 || char.IsDigit(value[0]) || value[0] is '+' or '-' ||
            !Enum.TryParse(value, ignoreCase: false, out T parsed) || !Enum.IsDefined(parsed) || parsed.ToString() != value)
        {
            throw new InvalidDataException($"{field} has invalid named value '{value ?? "<missing>"}'.");
        }

        return parsed;
    }

    private static T RequireDefined<T>(T value) where T : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new InvalidDataException($"Cannot persist undefined {typeof(T).Name} value '{value}'.");

    private static bool LocationsEqual(GameLocationSettings location, string? scalarPath, IReadOnlyList<string> scalarRoots) =>
        string.Equals(location.InstallationLocation, scalarPath, StringComparison.Ordinal) &&
        location.ModRootLocations.SequenceEqual(scalarRoots, StringComparer.Ordinal);
}
