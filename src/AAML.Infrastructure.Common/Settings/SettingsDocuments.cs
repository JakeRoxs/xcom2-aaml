using Newtonsoft.Json;

namespace AAML.Infrastructure.Common.Settings;

internal abstract class SettingsDocumentBase
{
    [JsonProperty("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonProperty("selectedGame")]
    public string? SelectedGame { get; init; }

    [JsonProperty("gameInstallationLocation")]
    public string? GameInstallationLocation { get; init; }

    [JsonProperty("modRootLocations")]
    public IReadOnlyList<string>? ModRootLocations { get; init; }

    [JsonProperty("launchArguments")]
    public IReadOnlyList<string>? LaunchArguments { get; init; }

    [JsonProperty("modIntents")]
    public IReadOnlyList<ModIntentDocument>? ModIntents { get; init; }

    [JsonProperty("categories")]
    public IReadOnlyList<CategoryDocument>? Categories { get; init; }

    [JsonProperty("tags")]
    public IReadOnlyList<TagDocument>? Tags { get; init; }
}

internal sealed class SettingsDocumentV1 : SettingsDocumentBase;

internal class SettingsDocumentV2 : SettingsDocumentBase
{
    [JsonProperty("allowLaunchWithMissingDependencies")]
    public bool AllowLaunchWithMissingDependencies { get; init; }
}

internal class SettingsDocumentV3 : SettingsDocumentV2
{
    [JsonProperty("gameLocations")]
    public IReadOnlyList<GameLocationDocument>? GameLocations { get; init; }

    [JsonProperty("closeAfterLaunch")]
    public bool CloseAfterLaunch { get; init; }

    [JsonProperty("workshopStartupRefresh")]
    public string? WorkshopStartupRefresh { get; init; }

    [JsonProperty("theme")]
    public string? Theme { get; init; }

    [JsonProperty("allowMultipleInstances")]
    public bool AllowMultipleInstances { get; init; }
}

internal class SettingsDocumentV4 : SettingsDocumentV3
{
    [JsonProperty("duplicatePreferences")]
    public IReadOnlyList<DuplicatePreferenceDocument>? DuplicatePreferences { get; init; }
}

internal class SettingsDocumentV5 : SettingsDocumentV4
{
    [JsonProperty("modGrid")]
    public ModGridDocument? ModGrid { get; init; }

    [JsonProperty("retainedWorkshopItems")]
    public IReadOnlyList<RetainedWorkshopDocument>? RetainedWorkshopItems { get; init; }
}

internal class SettingsDocumentV6 : SettingsDocumentV5
{
    [JsonProperty("checkForUpdates")]
    public bool? CheckForUpdates { get; init; }

    [JsonProperty("updateChannel")]
    public string? UpdateChannel { get; init; }
}

internal class SettingsDocumentV7 : SettingsDocumentV6;

internal sealed class SettingsDocumentV8 : SettingsDocumentV7
{
    [JsonProperty("navigationRailMode")]
    public string? NavigationRailMode { get; init; }
}

internal sealed record CurrentSettingsDocument(
    [property: JsonProperty("schemaVersion")] int SchemaVersion,
    [property: JsonProperty("selectedGame")] string SelectedGame,
    [property: JsonProperty("gameInstallationLocation")] string? GameInstallationLocation,
    [property: JsonProperty("modRootLocations")] IReadOnlyList<string> ModRootLocations,
    [property: JsonProperty("launchArguments")] IReadOnlyList<string> LaunchArguments,
    [property: JsonProperty("modIntents")] IReadOnlyList<ModIntentDocument> ModIntents,
    [property: JsonProperty("categories")] IReadOnlyList<CategoryDocument> Categories,
    [property: JsonProperty("tags")] IReadOnlyList<TagDocument> Tags,
    [property: JsonProperty("allowLaunchWithMissingDependencies")] bool AllowLaunchWithMissingDependencies,
    [property: JsonProperty("gameLocations")] IReadOnlyList<GameLocationDocument> GameLocations,
    [property: JsonProperty("closeAfterLaunch")] bool CloseAfterLaunch,
    [property: JsonProperty("workshopStartupRefresh")] string WorkshopStartupRefresh,
    [property: JsonProperty("theme")] string Theme,
    [property: JsonProperty("allowMultipleInstances")] bool AllowMultipleInstances,
    [property: JsonProperty("duplicatePreferences")] IReadOnlyList<DuplicatePreferenceDocument> DuplicatePreferences,
    [property: JsonProperty("modGrid")] ModGridDocument ModGrid,
    [property: JsonProperty("retainedWorkshopItems")] IReadOnlyList<RetainedWorkshopDocument> RetainedWorkshopItems,
    [property: JsonProperty("checkForUpdates")] bool CheckForUpdates,
    [property: JsonProperty("updateChannel")] string UpdateChannel,
    [property: JsonProperty("navigationRailMode")] string NavigationRailMode,
    [property: JsonProperty("autoSaveChanges", Required = Required.Always)] bool AutoSaveChanges);

internal sealed record DuplicatePreferenceDocument(string PackageId, string Source, string LocationIdentity);

internal sealed class ModGridDocument
{
    [JsonProperty("includeHidden")]
    public bool? IncludeHidden { get; init; }

    [JsonProperty("stateFilter")]
    public string? StateFilter { get; init; }

    [JsonProperty("groupByCategory")]
    public bool? GroupByCategory { get; init; }

    [JsonProperty("collapsedGroups")]
    public IReadOnlyList<ModGridGroupDocument>? CollapsedGroups { get; init; }
}

internal sealed record ModGridGroupDocument(
    [property: JsonProperty("grouping")] string Grouping,
    [property: JsonProperty("bucket")] string Bucket);

internal sealed record RetainedWorkshopDocument(ulong WorkshopId, string PackageId, string Name, string Source, string LocationIdentity);

internal sealed record GameLocationDocument(
    [property: JsonProperty("game")] string Game,
    [property: JsonProperty("installationLocation")] string? InstallationLocation,
    [property: JsonProperty("modRootLocations")] IReadOnlyList<string>? ModRootLocations);

internal sealed record ModIntentDocument(
    [property: JsonProperty("source")] string Source,
    [property: JsonProperty("locationIdentity")] string LocationIdentity,
    [property: JsonProperty("isActive")] bool IsActive,
    [property: JsonProperty("isHidden")] bool IsHidden,
    [property: JsonProperty("explicitOrder")] int? ExplicitOrder,
    [property: JsonProperty("manualName")] string? ManualName,
    [property: JsonProperty("categoryId")] string? CategoryId,
    [property: JsonProperty("tagIds")] IReadOnlyList<string>? TagIds,
    [property: JsonProperty("note")] string? Note,
    [property: JsonProperty("ignoredDependencies")] IReadOnlyList<ulong>? IgnoredDependencies);

internal sealed record CategoryDocument(string Id, string Name, int Order);
internal sealed record TagDocument(string Id, string Name, string? Color = null);
