using AAML.Application.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AAML.Infrastructure.Common.Settings;

internal sealed class SettingsMigrationState
{
    public required int SourceSchemaVersion { get; init; }
    public required string SelectedGame { get; init; }
    public string? GameInstallationLocation { get; init; }
    public IReadOnlyList<string>? ModRootLocations { get; init; }
    public IReadOnlyList<string>? LaunchArguments { get; init; }
    public IReadOnlyList<ModIntentDocument>? ModIntents { get; init; }
    public IReadOnlyList<CategoryDocument>? Categories { get; init; }
    public IReadOnlyList<TagDocument>? Tags { get; init; }
    public bool AllowLaunchWithMissingDependencies { get; set; }
    public IReadOnlyList<GameLocationDocument>? GameLocations { get; set; }
    public bool CloseAfterLaunch { get; set; }
    public string? WorkshopStartupRefresh { get; set; }
    public string? Theme { get; set; }
    public bool AllowMultipleInstances { get; set; }
    public IReadOnlyList<DuplicatePreferenceDocument>? DuplicatePreferences { get; set; }
    public ModGridDocument? ModGrid { get; set; }
    public IReadOnlyList<RetainedWorkshopDocument>? RetainedWorkshopItems { get; set; }
    public bool? CheckForUpdates { get; set; }
    public string? UpdateChannel { get; set; }
    public string? NavigationRailMode { get; set; }
    public bool AutoSaveChanges { get; set; }
    public decimal TextScale { get; set; }
    public decimal IconScale { get; set; }
}

internal sealed record SettingsReadResult(AAML.Application.Settings.ApplicationSettings Settings, int SourceSchemaVersion, bool RequiresCanonicalRewrite);

internal static class SettingsSchemaMigrator
{
    public static SettingsReadResult Read(string json)
    {
        var root = JObject.Parse(json);
        if (root["schemaVersion"]?.Type != JTokenType.Integer)
            throw new InvalidDataException("settings schemaVersion must be an integer.");
        var schema = root.Value<int>("schemaVersion");
        if (schema is < 1 or > ApplicationSettingsDefaults.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported settings schema: {schema}.");

        if (schema == ApplicationSettingsDefaults.CurrentSchemaVersion)
        {
            ValidateCanonical(root);
            var current = Deserialize<CurrentSettingsDocument>(json, schema);
            return new SettingsReadResult(ApplicationSettingsMapper.FromCurrentDocument(current), schema, false);
        }

        if (schema == 9)
        {
            ValidateCanonical(root, 9);
            root["schemaVersion"] = ApplicationSettingsDefaults.CurrentSchemaVersion;
            root["textScale"] = ApplicationSettingsDefaults.DefaultTextScale;
            root["iconScale"] = ApplicationSettingsDefaults.DefaultIconScale;
            var migrated = Deserialize<CurrentSettingsDocument>(root.ToString(Formatting.None), ApplicationSettingsDefaults.CurrentSchemaVersion);
            return new SettingsReadResult(ApplicationSettingsMapper.FromCurrentDocument(migrated), schema, true);
        }

        var state = schema switch
        {
            1 => Migrate1To2(FromV1(Deserialize<SettingsDocumentV1>(json, schema))),
            2 => FromV2(Deserialize<SettingsDocumentV2>(json, schema)),
            3 => FromV3(Deserialize<SettingsDocumentV3>(json, schema)),
            4 => FromV4(Deserialize<SettingsDocumentV4>(json, schema)),
            5 => FromV5(Deserialize<SettingsDocumentV5>(json, schema)),
            6 => FromV6(Deserialize<SettingsDocumentV6>(json, schema)),
            7 => FromV7(Deserialize<SettingsDocumentV7>(json, schema)),
            8 => FromV8(Deserialize<SettingsDocumentV8>(json, schema)),
            _ => throw new InvalidDataException($"Unsupported settings schema: {schema}.")
        };

        if (schema <= 2) state = Migrate2To3(state);
        if (schema <= 3) state = Migrate3To4(state);
        if (schema <= 4) state = Migrate4To5(state);
        if (schema <= 5) state = Migrate5To6(state);
        if (schema <= 6) state = Migrate6To7(state);
        if (schema <= 7) state = Migrate7To8(state);
        if (schema <= 8) state = Migrate8To9(state);
        state = Migrate9To10(state);
        return new SettingsReadResult(ApplicationSettingsMapper.FromMigratedDocument(state), schema, true);
    }

    private static T Deserialize<T>(string json, int expectedSchema)
    {
        var document = JsonConvert.DeserializeObject<T>(json) ?? throw new JsonSerializationException("Settings were null.");
        if (document is SettingsDocumentBase settings && settings.SchemaVersion != expectedSchema)
            throw new InvalidDataException($"Settings schema {expectedSchema} could not be read by its compatibility contract.");
        return document;
    }

    private static void ValidateCanonical(JObject root, int schema = ApplicationSettingsDefaults.CurrentSchemaVersion)
    {
        string[] expected =
        [
            "schemaVersion", "selectedGame", "gameInstallationLocation", "modRootLocations", "launchArguments",
            "modIntents", "categories", "tags", "allowLaunchWithMissingDependencies", "gameLocations",
            "closeAfterLaunch", "workshopStartupRefresh", "theme", "allowMultipleInstances", "duplicatePreferences",
            "modGrid", "retainedWorkshopItems", "checkForUpdates", "updateChannel", "navigationRailMode", "autoSaveChanges"
        ];
        if (schema >= 10) expected = [.. expected, "textScale", "iconScale"];
        if (!root.Properties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidDataException($"Schema {schema} must contain exactly the canonical settings members.");

        RequireType(root, JTokenType.String, "selectedGame", "workshopStartupRefresh", "theme", "updateChannel", "navigationRailMode");
        RequireType(root, JTokenType.Array, "modRootLocations", "launchArguments", "modIntents", "categories", "tags", "gameLocations", "duplicatePreferences", "retainedWorkshopItems");
        RequireType(root, JTokenType.Boolean, "allowLaunchWithMissingDependencies", "closeAfterLaunch", "allowMultipleInstances", "checkForUpdates", "autoSaveChanges");
        if (schema >= 10)
        {
            RequireNumber(root, "textScale", "iconScale");
            var textScale = root.Value<decimal>("textScale");
            var iconScale = root.Value<decimal>("iconScale");
            if (!ApplicationSettingsDefaults.IsTextScaleSupported(textScale)) throw new InvalidDataException("Schema 10 textScale is outside the supported range.");
            if (!ApplicationSettingsDefaults.IsIconScaleSupported(iconScale)) throw new InvalidDataException("Schema 10 iconScale is outside the supported range.");
        }
        if (root["gameInstallationLocation"]?.Type is not (JTokenType.String or JTokenType.Null))
            throw new InvalidDataException($"Schema {schema} gameInstallationLocation must be a string or null.");

        if (root["modGrid"] is not JObject grid) throw new InvalidDataException($"Schema {schema} modGrid must be an object.");
        string[] gridMembers = ["includeHidden", "stateFilter", "groupByCategory", "collapsedGroups"];
        if (!grid.Properties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(gridMembers))
            throw new InvalidDataException($"Schema {schema} modGrid must contain exactly the canonical members.");
        RequireType(grid, JTokenType.Boolean, "includeHidden", "groupByCategory");
        RequireType(grid, JTokenType.Array, "collapsedGroups");
        if (grid["stateFilter"]?.Type is not (JTokenType.String or JTokenType.Null))
            throw new InvalidDataException($"Schema {schema} modGrid.stateFilter must be a string or null.");
    }

    private static void RequireType(JObject root, JTokenType type, params string[] properties)
    {
        foreach (var property in properties)
            if (root[property]?.Type != type) throw new InvalidDataException($"Canonical settings {property} must be {type}.");
    }

    private static void RequireNumber(JObject root, params string[] properties)
    {
        foreach (var property in properties)
            if (root[property]?.Type is not (JTokenType.Integer or JTokenType.Float)) throw new InvalidDataException($"Schema 10 {property} must be numeric.");
    }

    private static SettingsMigrationState FromV1(SettingsDocumentV1 document) => Common(document);

    private static SettingsMigrationState FromV2(SettingsDocumentV2 document)
    {
        var state = Common(document);
        state.AllowLaunchWithMissingDependencies = document.AllowLaunchWithMissingDependencies;
        return state;
    }

    private static SettingsMigrationState FromV3(SettingsDocumentV3 document)
    {
        var state = FromV2(document);
        state.GameLocations = document.GameLocations;
        state.CloseAfterLaunch = document.CloseAfterLaunch;
        state.WorkshopStartupRefresh = document.WorkshopStartupRefresh;
        state.Theme = document.Theme;
        state.AllowMultipleInstances = document.AllowMultipleInstances;
        return state;
    }

    private static SettingsMigrationState FromV4(SettingsDocumentV4 document)
    {
        var state = FromV3(document);
        state.DuplicatePreferences = document.DuplicatePreferences;
        return state;
    }

    private static SettingsMigrationState FromV5(SettingsDocumentV5 document)
    {
        var state = FromV4(document);
        state.ModGrid = document.ModGrid;
        state.RetainedWorkshopItems = document.RetainedWorkshopItems;
        return state;
    }

    private static SettingsMigrationState FromV6(SettingsDocumentV6 document)
    {
        var state = FromV5(document);
        state.CheckForUpdates = document.CheckForUpdates;
        state.UpdateChannel = document.UpdateChannel;
        return state;
    }

    private static SettingsMigrationState FromV7(SettingsDocumentV7 document) => FromV6(document);
    private static SettingsMigrationState FromV8(SettingsDocumentV8 document)
    {
        var state = FromV7(document);
        state.NavigationRailMode = document.NavigationRailMode ?? throw new InvalidDataException("Schema 8 navigationRailMode is required.");
        return state;
    }

    private static SettingsMigrationState Common(SettingsDocumentBase document)
    {
        if (string.IsNullOrEmpty(document.SelectedGame)) throw new InvalidDataException("selectedGame is required.");
        return new SettingsMigrationState
        {
            SourceSchemaVersion = document.SchemaVersion,
            SelectedGame = document.SelectedGame,
            GameInstallationLocation = document.GameInstallationLocation,
            ModRootLocations = document.ModRootLocations ?? [],
            LaunchArguments = document.LaunchArguments ?? [],
            ModIntents = document.ModIntents ?? [],
            Categories = document.Categories ?? [],
            Tags = document.Tags ?? []
        };
    }

    private static SettingsMigrationState Migrate1To2(SettingsMigrationState state)
    {
        state.AllowLaunchWithMissingDependencies = false;
        return state;
    }

    private static SettingsMigrationState Migrate2To3(SettingsMigrationState state)
    {
        state.GameLocations = [];
        state.WorkshopStartupRefresh = null;
        state.Theme = null;
        return state;
    }

    private static SettingsMigrationState Migrate3To4(SettingsMigrationState state)
    {
        state.DuplicatePreferences = [];
        return state;
    }

    private static SettingsMigrationState Migrate4To5(SettingsMigrationState state)
    {
        state.ModGrid = null;
        state.RetainedWorkshopItems = [];
        return state;
    }

    private static SettingsMigrationState Migrate5To6(SettingsMigrationState state)
    {
        state.CheckForUpdates = true;
        state.UpdateChannel = null;
        return state;
    }

    private static SettingsMigrationState Migrate6To7(SettingsMigrationState state)
    {
        state.GameLocations ??= [];
        state.DuplicatePreferences ??= [];
        state.RetainedWorkshopItems ??= [];
        state.CheckForUpdates ??= true;
        return state;
    }

    private static SettingsMigrationState Migrate7To8(SettingsMigrationState state)
    {
        state.NavigationRailMode = nameof(AAML.Application.Settings.NavigationRailMode.Expanded);
        return state;
    }

    private static SettingsMigrationState Migrate8To9(SettingsMigrationState state)
    {
        state.AutoSaveChanges = false;
        return state;
    }

    private static SettingsMigrationState Migrate9To10(SettingsMigrationState state)
    {
        state.TextScale = ApplicationSettingsDefaults.DefaultTextScale;
        state.IconScale = ApplicationSettingsDefaults.DefaultIconScale;
        return state;
    }
}
