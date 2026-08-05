using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Application.Mods.Grid;

namespace AAML.Infrastructure.Common.Settings;

internal static class ApplicationSettingsMapper
{
    public static SettingsDocumentV1 ToDocument(ApplicationSettings settings) => new(
        ApplicationSettingsDefaults.CurrentSchemaVersion,
        settings.SelectedGame.ToString(),
        settings.GameInstallationLocation,
        settings.ModRootLocations.ToArray(),
        settings.LaunchArguments.Select(argument => argument.Value).ToArray(),
        settings.ModIntents.Select(intent => new ModIntentDocumentV1(
            intent.Mod.Source.ToString(),
            intent.Mod.LocationIdentity,
            intent.IsActive,
            intent.IsHidden,
            intent.ExplicitOrder,
            intent.ManualName,
            intent.Category?.Value,
            intent.Tags.Select(tag => tag.Value).ToArray(),
            intent.Note,
            intent.IgnoredDependencies.Select(id => id.Value).ToArray())).ToArray(),
        settings.Categories.Select(category => new CategoryDocumentV1(category.Id.Value, category.Name, category.Order)).ToArray(),
        settings.Tags.Select(tag => new TagDocumentV1(tag.Id.Value, tag.Name, tag.Color)).ToArray(),
        settings.AllowLaunchWithMissingDependencies,
        Enum.GetValues<GameVariant>().Select(game => (Game: game, Location: settings.LocationFor(game))).Where(item => item.Location.InstallationLocation is not null || item.Location.ModRootLocations.Count > 0).Select(item => new GameLocationDocument(item.Game.ToString(), item.Location.InstallationLocation, item.Location.ModRootLocations)).ToArray(),
        settings.CloseAfterLaunch,
        settings.WorkshopStartupRefresh.ToString(),
        settings.Theme.ToString(),
        settings.AllowMultipleInstances,
        (settings.DuplicatePreferences ?? []).Select(preference => new DuplicatePreferenceDocument(preference.PackageId.Value, preference.PreferredInstallation.Source.ToString(), preference.PreferredInstallation.LocationIdentity)).ToArray(),
        settings.ModGrid is null ? null : new ModGridDocument(settings.ModGrid.IncludeHidden, settings.ModGrid.StateFilter?.ToString(), settings.ModGrid.GroupByCategory, settings.ModGrid.CollapsedGroups.Select(key => new ModGridGroupDocument(key.Grouping, key.Bucket)).ToArray()),
        (settings.RetainedWorkshopItems ?? []).Select(item => new RetainedWorkshopDocument(item.WorkshopId.Value, item.PackageId.Value, item.Name, item.LastKnownKey.Source.ToString(), item.LastKnownKey.LocationIdentity)).ToArray(),
        settings.CheckForUpdates,
        settings.UpdateChannel.ToString());

    public static ApplicationSettings FromDocument(SettingsDocumentV1 document)
    {
        if (document.SchemaVersion is < 1 or > ApplicationSettingsDefaults.CurrentSchemaVersion || !Enum.TryParse<GameVariant>(document.SelectedGame, out var game))
        {
            throw new InvalidDataException("Unsupported or invalid settings document.");
        }

        var persistedArguments = document.LaunchArguments ?? [];
        var launchArguments = document.SchemaVersion == 1 && persistedArguments.Count == 0
            ? ApplicationSettingsDefaults.LaunchArguments
            : persistedArguments.Select(argument => new LaunchArgument(argument)).ToArray();
        return new ApplicationSettings(
            ApplicationSettingsDefaults.CurrentSchemaVersion,
            game,
            document.GameInstallationLocation,
            document.ModRootLocations ?? [],
            launchArguments,
            (document.ModIntents ?? []).Select(intent => new ModUserIntent(
                new ModKey(Enum.Parse<ModSource>(intent.Source), intent.LocationIdentity),
                intent.IsActive,
                intent.IsHidden,
                intent.ExplicitOrder,
                intent.ManualName,
                string.IsNullOrWhiteSpace(intent.CategoryId) ? null : new CategoryId(intent.CategoryId),
                (intent.TagIds ?? []).Select(id => new TagId(id)).ToHashSet(),
                intent.Note,
                (intent.IgnoredDependencies ?? []).Select(id => new WorkshopId(id)).ToHashSet())).ToArray(),
            (document.Categories ?? []).Select(category => new Category(new CategoryId(category.Id), category.Name, category.Order)).ToArray(),
            (document.Tags ?? []).Select(tag => new Tag(new TagId(tag.Id), tag.Name, tag.Color)).ToArray(),
            document.AllowLaunchWithMissingDependencies,
            ParseLocations(document),
            document.CloseAfterLaunch,
            Enum.TryParse<WorkshopStartupRefreshPolicy>(document.WorkshopStartupRefresh, true, out var refresh) && refresh != WorkshopStartupRefreshPolicy.Manual ? refresh : WorkshopStartupRefreshPolicy.AllMods,
            Enum.TryParse<ThemePreference>(document.Theme, true, out var theme) ? theme : ThemePreference.System,
            document.AllowMultipleInstances,
            (document.DuplicatePreferences ?? []).Select(preference => new DuplicatePreference(new PackageId(preference.PackageId), new ModKey(Enum.Parse<ModSource>(preference.Source), preference.LocationIdentity))).ToArray(),
            document.ModGrid is null ? ModGridPreferences.Default : new ModGridPreferences(document.ModGrid.IncludeHidden, Enum.TryParse<ModGridSemanticState>(document.ModGrid.StateFilter, out var filter) ? filter : null, document.ModGrid.GroupByCategory, (document.ModGrid.CollapsedGroups ?? []).Select(key => new ModGridGroupKey(key.Grouping, key.Bucket)).ToHashSet()),
            (document.RetainedWorkshopItems ?? []).Select(item => new RetainedWorkshopItem(new WorkshopId(item.WorkshopId), new PackageId(item.PackageId), item.Name, new ModKey(Enum.Parse<ModSource>(item.Source), item.LocationIdentity))).ToArray(),
            document.CheckForUpdates ?? true,
            Enum.TryParse<UpdateChannelPreference>(document.UpdateChannel, true, out var channel) ? channel : UpdateChannelPreference.Stable);
    }

    private static IReadOnlyDictionary<GameVariant, GameLocationSettings> ParseLocations(SettingsDocumentV1 document)
    {
        var result = new Dictionary<GameVariant, GameLocationSettings>();
        foreach (var item in document.GameLocations ?? [])
            if (Enum.TryParse<GameVariant>(item.Game, true, out var game)) result[game] = new GameLocationSettings(item.InstallationLocation, item.ModRootLocations ?? []);
        if (result.Count == 0 && Enum.TryParse<GameVariant>(document.SelectedGame, true, out var selected)) result[selected] = new GameLocationSettings(document.GameInstallationLocation, document.ModRootLocations ?? []);
        return result;
    }
}
