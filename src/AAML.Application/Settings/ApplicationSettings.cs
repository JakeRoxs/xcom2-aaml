using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Application.Mods.Grid;

namespace AAML.Application.Settings;

/// <summary>Platform-neutral durable application settings.</summary>
public sealed record ApplicationSettings(
    int SchemaVersion,
    GameVariant SelectedGame,
    string? GameInstallationLocation,
    IReadOnlyList<string> ModRootLocations,
    IReadOnlyList<LaunchArgument> LaunchArguments,
    IReadOnlyList<ModUserIntent> ModIntents,
    IReadOnlyList<Category> Categories,
    IReadOnlyList<Tag> Tags,
    bool AllowLaunchWithMissingDependencies = false,
    IReadOnlyDictionary<GameVariant, GameLocationSettings>? GameLocations = null,
    bool CloseAfterLaunch = false,
    WorkshopStartupRefreshPolicy WorkshopStartupRefresh = WorkshopStartupRefreshPolicy.AllMods,
    ThemePreference Theme = ThemePreference.System,
    bool AllowMultipleInstances = false,
    IReadOnlyList<DuplicatePreference>? DuplicatePreferences = null,
    ModGridPreferences? ModGrid = null,
    IReadOnlyList<RetainedWorkshopItem>? RetainedWorkshopItems = null,
    bool CheckForUpdates = true,
    UpdateChannelPreference UpdateChannel = UpdateChannelPreference.Stable,
    NavigationRailMode NavigationRailMode = NavigationRailMode.Expanded,
    bool AutoSaveChanges = false,
    decimal TextScale = ApplicationSettingsDefaults.DefaultTextScale,
    decimal IconScale = ApplicationSettingsDefaults.DefaultIconScale)
{
    public GameLocationSettings LocationFor(GameVariant variant)
    {
        if (GameLocations?.TryGetValue(variant, out var location) == true) return location;
        return variant == SelectedGame ? new GameLocationSettings(GameInstallationLocation, ModRootLocations) : new GameLocationSettings(null, []);
    }
}

public sealed record GameLocationSettings(string? InstallationLocation, IReadOnlyList<string> ModRootLocations);
public enum WorkshopStartupRefreshPolicy { Manual, ActiveMods, AllMods }
public enum ThemePreference { System, Light, Dark }
public enum UpdateChannelPreference { Stable, Prerelease, Alpha }
public enum NavigationRailMode { Expanded, Compact }
public sealed record ModGridPreferences(bool IncludeHidden, ModGridSemanticState? StateFilter, bool GroupByCategory, IReadOnlySet<ModGridGroupKey> CollapsedGroups)
{
    public static ModGridPreferences Default { get; } = new(true, null, false, new HashSet<ModGridGroupKey>());
}
