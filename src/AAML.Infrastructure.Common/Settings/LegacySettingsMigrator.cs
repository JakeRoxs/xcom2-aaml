using AAML.Application.Common;
using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Compatibility.Settings;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AAML.Infrastructure.Common.Settings;

/// <summary>Migrates supported durable intent while discarding non-domain legacy state.</summary>
public static class LegacySettingsMigrator
{
    private static readonly string[] DefaultQuickToggleArguments = ["-review", "-noRedScreens", "-noStartUpMovies", "-allowConsole", "-regenerateinis"];

    public static Result<LegacySettingsMigration> Migrate(string json, Func<ModSource, string, Result<string>> normalizeLocation)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(normalizeLocation);

        try
        {
            var root = JObject.Parse(json);
            var diagnostics = new List<LegacySettingsMigrationDiagnostic>();
            var game = root.Value<uint?>("Game") switch
            {
                882100 => GameVariant.ChimeraSquad,
                268500 when root.Value<bool?>("LastLaunchedWotC") == true => GameVariant.XCom2WarOfTheChosen,
                _ => GameVariant.XCom2
            };
            var categories = new List<Category>();
            var tags = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
            var intents = new List<ModUserIntent>();
            foreach (var property in (root["Tags"] as JObject)?.Properties() ?? [])
            {
                var name = property.Value.Value<string>("Label") ?? property.Name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var tag = AddTag(tags, name);
                    if (property.Value is JObject tagObject)
                    {
                        var color = ReadColor(tagObject, $"Tags.{property.Name}.Color", diagnostics);
                        if (color is not null) tags[tag.Name] = tag with { Color = color };
                    }
                }
            }
            var categoryEntries = root["Mods"]?["Entries"] as JObject;
            if (categoryEntries is not null)
            {
                var categoryOrdinal = 0;
                foreach (var categoryProperty in categoryEntries.Properties())
                {
                    var categoryId = CategoryIdentity(categoryProperty.Name);
                    var legacyOrder = (categoryProperty.Value as JObject)?.Value<int?>("Index");
                    var categoryOrder = legacyOrder is >= 0 ? legacyOrder.Value : categoryOrdinal;
                    categories.Add(new Category(categoryId, categoryProperty.Name, categoryOrder));
                    var mods = categoryProperty.Value as JArray ?? categoryProperty.Value["Entries"] as JArray;
                    if (mods is not null)
                    {
                        foreach (var mod in mods.OfType<JObject>())
                        {
                            AddIntent(mod, categoryId, normalizeLocation, tags, intents);
                        }
                    }

                    categoryOrdinal++;
                }
            }
            else if (root["Mods"] is JObject legacyMods)
            {
                var categoryOrdinal = 0;
                foreach (var property in legacyMods.Properties())
                {
                    if (property.Value is not JArray mods) continue;
                    var categoryId = CategoryIdentity(property.Name);
                    categories.Add(new Category(categoryId, property.Name, categoryOrdinal++));
                    foreach (var mod in mods.OfType<JObject>())
                    {
                        AddIntent(mod, categoryId, normalizeLocation, tags, intents);
                    }
                }
            }

            var settings = new ApplicationSettings(
                ApplicationSettingsDefaults.CurrentSchemaVersion,
                game,
                NormalizeOptionalPath(root.Value<string>("GamePath"), normalizeLocation),
                NormalizeRoots(root["ModPaths"]?.Values<string>() ?? [], normalizeLocation),
                LegacySettingsArguments.Read(json).Select(argument => new LaunchArgument(argument)).ToArray(),
                intents,
                categories,
                tags.Values.ToArray(),
                false,
                CloseAfterLaunch: ReadBoolean(root, "CloseAfterLaunch", false, diagnostics),
                WorkshopStartupRefresh: ReadWorkshopPolicy(root, diagnostics),
                Theme: ReadBoolean(root, "DarkMode", false, diagnostics) ? ThemePreference.Dark : ThemePreference.Light,
                AllowMultipleInstances: ReadBoolean(root, "AllowMultipleInstances", false, diagnostics),
                ModGrid: new ModGridPreferences(
                    ReadBoolean(root, "ShowHiddenElements", false, diagnostics),
                    null,
                    ReadBoolean(root, "ShowModListGroups", true, diagnostics),
                    new HashSet<AAML.Application.Mods.Grid.ModGridGroupKey>()),
                CheckForUpdates: ReadBoolean(root, "CheckForUpdates", true, diagnostics),
                UpdateChannel: ReadUpdateChannel(root, diagnostics));
            var report = new LegacySettingsMigrationReport(
                1,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),
                true,
                ReadQuickToggleArguments(root, diagnostics),
                diagnostics);
            return Result<LegacySettingsMigration>.Success(new LegacySettingsMigration(settings, report));
        }
        catch (Exception exception) when (exception is Newtonsoft.Json.JsonException or InvalidDataException or ArgumentException)
        {
            return Result<LegacySettingsMigration>.Failure(new Error("settings.migration_failed", exception.Message, ErrorKind.InvalidData));
        }
    }

    private static bool ReadBoolean(JObject root, string property, bool defaultValue, List<LegacySettingsMigrationDiagnostic> diagnostics)
    {
        var token = root[property];
        if (token is null) return defaultValue;
        if (token.Type == JTokenType.Boolean) return token.Value<bool>();
        diagnostics.Add(Diagnostic("legacy_settings.invalid_boolean", property, $"{property} was not a boolean; the legacy default '{defaultValue}' was used."));
        return defaultValue;
    }

    private static WorkshopStartupRefreshPolicy ReadWorkshopPolicy(JObject root, List<LegacySettingsMigrationDiagnostic> diagnostics)
    {
        var enabled = ReadBoolean(root, "UpdateModsOnStartup", true, diagnostics);
        var activeOnly = ReadBoolean(root, "OnlyUpdateEnabledOrNewModsOnStartup", false, diagnostics);
        return !enabled ? WorkshopStartupRefreshPolicy.Manual : activeOnly ? WorkshopStartupRefreshPolicy.ActiveMods : WorkshopStartupRefreshPolicy.AllMods;
    }

    private static UpdateChannelPreference ReadUpdateChannel(JObject root, List<LegacySettingsMigrationDiagnostic> diagnostics)
    {
        var prerelease = ReadBoolean(root, "CheckForPreReleaseUpdates", false, diagnostics);
        var alpha = ReadBoolean(root, "IncludeAlphaVersions", false, diagnostics);
        if (alpha && !prerelease)
            diagnostics.Add(Diagnostic("legacy_settings.dormant_alpha_preference", "IncludeAlphaVersions", "IncludeAlphaVersions was enabled while prerelease checks were disabled; effective legacy behavior remained Stable."));
        return prerelease ? alpha ? UpdateChannelPreference.Alpha : UpdateChannelPreference.Prerelease : UpdateChannelPreference.Stable;
    }

    private static IReadOnlyList<string> ReadQuickToggleArguments(JObject root, List<LegacySettingsMigrationDiagnostic> diagnostics)
    {
        var token = root["QuickToggleArguments"];
        if (token is null) return DefaultQuickToggleArguments;
        if (token is not JArray array)
        {
            diagnostics.Add(Diagnostic("legacy_settings.invalid_quick_toggle_arguments", "QuickToggleArguments", "QuickToggleArguments was not an array; no quick-toggle metadata was retained."));
            return [];
        }

        var arguments = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(array[index]!.Value<string>()))
            {
                diagnostics.Add(Diagnostic("legacy_settings.invalid_quick_toggle_argument", $"QuickToggleArguments[{index}]", "The quick-toggle argument was not a non-empty string and was omitted."));
                continue;
            }

            var argument = array[index]!.Value<string>()!.Trim();
            if (seen.Add(argument)) arguments.Add(argument);
        }
        return arguments;
    }

    private static string? ReadColor(JObject tag, string path, List<LegacySettingsMigrationDiagnostic> diagnostics)
    {
        var token = tag["Color"];
        if (token is null) return null;
        if (token.Type != JTokenType.String || !TryNormalizeColor(token.Value<string>(), out var normalized))
        {
            diagnostics.Add(Diagnostic("legacy_settings.invalid_tag_color", path, "The legacy tag color was invalid and was omitted."));
            return null;
        }
        return normalized;
    }

    private static bool TryNormalizeColor(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        byte alpha = byte.MaxValue;
        byte red = 0;
        byte green = 0;
        byte blue = 0;
        var numeric = parts.Length == 3 && TryByte(parts[0], out red) && TryByte(parts[1], out green) && TryByte(parts[2], out blue);
        if (!numeric && parts.Length == 4)
            numeric = TryByte(parts[0], out alpha) && TryByte(parts[1], out red) && TryByte(parts[2], out green) && TryByte(parts[3], out blue);
        if (!numeric)
        {
            var color = System.Drawing.Color.FromName(value.Trim());
            if (!color.IsKnownColor || color.IsSystemColor || color.IsEmpty) return false;
            alpha = color.A;
            red = color.R;
            green = color.G;
            blue = color.B;
        }

        normalized = alpha == byte.MaxValue
            ? $"#{red:X2}{green:X2}{blue:X2}"
            : $"#{red:X2}{green:X2}{blue:X2}{alpha:X2}";
        return true;
    }

    private static bool TryByte(string value, out byte result) => byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    private static LegacySettingsMigrationDiagnostic Diagnostic(string code, string path, string message) => new(code, path, message);

    private static void AddIntent(
        JObject mod,
        CategoryId? category,
        Func<ModSource, string, Result<string>> normalizeLocation,
        Dictionary<string, Tag> tags,
        List<ModUserIntent> intents)
    {
        var source = mod.Value<int?>("Source") switch
        {
            1 => ModSource.SteamWorkshop,
            4 => ModSource.Manual,
            _ => ModSource.Unknown
        };
        var path = mod.Value<string>("Path") ?? throw new InvalidDataException("A legacy mod omitted its path.");
        var normalized = normalizeLocation(source, path);
        if (!normalized.IsSuccess)
        {
            throw new InvalidDataException(normalized.Error!.Message);
        }

        var tagIds = new HashSet<TagId>();
        foreach (var tagName in mod["Tags"]?.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>() ?? [])
        {
            var tag = AddTag(tags, tagName);
            tagIds.Add(tag.Id);
        }

        intents.Add(new ModUserIntent(
            new ModKey(source, normalized.Value!),
            mod.Value<bool?>("isActive") ?? false,
            mod.Value<bool?>("isHidden") ?? false,
            mod.Value<int?>("Index") is >= 0 and var order ? order : null,
            mod.Value<bool?>("ManualName") == true ? mod.Value<string>("Name") : null,
            category,
            tagIds,
            mod.Value<string>("Note"),
            (mod["IgnoredDependencies"]?.Values<long>() ?? []).Where(id => id > 0).Select(id => new WorkshopId((ulong)id)).ToHashSet()));
    }

    private static Tag AddTag(Dictionary<string, Tag> tags, string name)
    {
        var normalized = name.Trim();
        if (tags.TryGetValue(normalized, out var existing)) return existing;
        var tag = new Tag(new TagId($"legacy-tag-{StableSuffix(normalized)}"), normalized);
        tags.Add(normalized, tag);
        return tag;
    }

    private static CategoryId CategoryIdentity(string name) => new($"legacy-category-{StableSuffix(name.Trim())}");

    private static string StableSuffix(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))[..16].ToLowerInvariant();

    private static string? NormalizeOptionalPath(string? path, Func<ModSource, string, Result<string>> normalizeLocation)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = normalizeLocation(ModSource.Manual, path);
        return normalized.IsSuccess ? normalized.Value : throw new InvalidDataException(normalized.Error!.Message);
    }

    private static IReadOnlyList<string> NormalizeRoots(IEnumerable<string?> roots, Func<ModSource, string, Result<string>> normalizeLocation) => roots
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => NormalizeOptionalPath(path, normalizeLocation)!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

/// <summary>Contains migrated schema-9 settings and source-preservation metadata.</summary>
public sealed record LegacySettingsMigration(ApplicationSettings Settings, LegacySettingsMigrationReport Report);

/// <summary>Versioned, non-settings metadata retained for migration auditing and future quick-toggle adoption.</summary>
public sealed record LegacySettingsMigrationReport(
    int SchemaVersion,
    string SourceSha256,
    bool SourcePreserved,
    IReadOnlyList<string> QuickToggleArguments,
    IReadOnlyList<LegacySettingsMigrationDiagnostic> Diagnostics);

/// <summary>Describes one recoverable legacy value that could not be represented.</summary>
public sealed record LegacySettingsMigrationDiagnostic(string Code, string Path, string Message);
