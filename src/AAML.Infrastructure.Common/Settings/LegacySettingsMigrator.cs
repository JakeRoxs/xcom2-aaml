using AAML.Application.Common;
using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Compatibility.Settings;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AAML.Infrastructure.Common.Settings;

/// <summary>Migrates supported durable intent while discarding non-domain legacy state.</summary>
public static class LegacySettingsMigrator
{
    public static Result<ApplicationSettings> Migrate(string json, Func<ModSource, string, Result<string>> normalizeLocation)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(normalizeLocation);

        try
        {
            var root = JObject.Parse(json);
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
                if (!string.IsNullOrWhiteSpace(name)) AddTag(tags, name);
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

            return Result<ApplicationSettings>.Success(new ApplicationSettings(
                ApplicationSettingsDefaults.CurrentSchemaVersion,
                game,
                NormalizeOptionalPath(root.Value<string>("GamePath"), normalizeLocation),
                NormalizeRoots(root["ModPaths"]?.Values<string>() ?? [], normalizeLocation),
                LegacySettingsArguments.Read(json).Select(argument => new LaunchArgument(argument)).ToArray(),
                intents,
                categories,
                tags.Values.ToArray(),
                false));
        }
        catch (Exception exception) when (exception is Newtonsoft.Json.JsonException or InvalidDataException or ArgumentException)
        {
            return Result<ApplicationSettings>.Failure(new Error("settings.migration_failed", exception.Message, ErrorKind.InvalidData));
        }
    }

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
