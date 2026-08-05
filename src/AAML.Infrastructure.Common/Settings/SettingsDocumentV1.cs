using Newtonsoft.Json;

namespace AAML.Infrastructure.Common.Settings;

internal sealed record SettingsDocumentV1(
    [property: JsonProperty("schemaVersion")] int SchemaVersion,
    [property: JsonProperty("selectedGame")] string SelectedGame,
    [property: JsonProperty("gameInstallationLocation")] string? GameInstallationLocation,
    [property: JsonProperty("modRootLocations")] IReadOnlyList<string> ModRootLocations,
    [property: JsonProperty("launchArguments")] IReadOnlyList<string> LaunchArguments,
    [property: JsonProperty("modIntents")] IReadOnlyList<ModIntentDocumentV1> ModIntents,
    [property: JsonProperty("categories")] IReadOnlyList<CategoryDocumentV1> Categories,
    [property: JsonProperty("tags")] IReadOnlyList<TagDocumentV1> Tags,
    [property: JsonProperty("allowLaunchWithMissingDependencies")] bool AllowLaunchWithMissingDependencies,
    [property: JsonProperty("gameLocations")] IReadOnlyList<GameLocationDocument>? GameLocations = null,
    [property: JsonProperty("closeAfterLaunch")] bool CloseAfterLaunch = false,
    [property: JsonProperty("workshopStartupRefresh")] string? WorkshopStartupRefresh = null,
    [property: JsonProperty("theme")] string? Theme = null,
    [property: JsonProperty("allowMultipleInstances")] bool AllowMultipleInstances = false,
    [property: JsonProperty("duplicatePreferences")] IReadOnlyList<DuplicatePreferenceDocument>? DuplicatePreferences = null,
    [property: JsonProperty("modGrid")] ModGridDocument? ModGrid = null,
    [property: JsonProperty("retainedWorkshopItems")] IReadOnlyList<RetainedWorkshopDocument>? RetainedWorkshopItems = null,
    [property: JsonProperty("checkForUpdates")] bool? CheckForUpdates = null,
    [property: JsonProperty("updateChannel")] string? UpdateChannel = null);

internal sealed record DuplicatePreferenceDocument(string PackageId, string Source, string LocationIdentity);
internal sealed record ModGridDocument([property: JsonProperty("includeHidden")] bool IncludeHidden, [property: JsonProperty("stateFilter")] string? StateFilter, [property: JsonProperty("groupByCategory")] bool GroupByCategory, [property: JsonProperty("collapsedGroups")] IReadOnlyList<ModGridGroupDocument> CollapsedGroups);
internal sealed record ModGridGroupDocument([property: JsonProperty("grouping")] string Grouping, [property: JsonProperty("bucket")] string Bucket);
internal sealed record RetainedWorkshopDocument(ulong WorkshopId, string PackageId, string Name, string Source, string LocationIdentity);

internal sealed record GameLocationDocument(
    [property: JsonProperty("game")] string Game,
    [property: JsonProperty("installationLocation")] string? InstallationLocation,
    [property: JsonProperty("modRootLocations")] IReadOnlyList<string> ModRootLocations);

internal sealed record ModIntentDocumentV1(
    [property: JsonProperty("source")] string Source,
    [property: JsonProperty("locationIdentity")] string LocationIdentity,
    [property: JsonProperty("isActive")] bool IsActive,
    [property: JsonProperty("isHidden")] bool IsHidden,
    [property: JsonProperty("explicitOrder")] int? ExplicitOrder,
    [property: JsonProperty("manualName")] string? ManualName,
    [property: JsonProperty("categoryId")] string? CategoryId,
    [property: JsonProperty("tagIds")] IReadOnlyList<string> TagIds,
    [property: JsonProperty("note")] string? Note,
    [property: JsonProperty("ignoredDependencies")] IReadOnlyList<ulong> IgnoredDependencies);

internal sealed record CategoryDocumentV1(string Id, string Name, int Order);
internal sealed record TagDocumentV1(string Id, string Name, string? Color = null);
