using AAML.Application.Common;
using AAML.Domain.Profiles;

namespace AAML.Application.Profiles;

public enum LegacyWorkshopIdStyle { Decimal, Url }
public sealed record LegacyProfileExportOptions(bool IncludeCategories, bool IncludeTags, LegacyWorkshopIdStyle WorkshopIdStyle);
public sealed record LegacyProfileExportResult(string Contents, IReadOnlyList<string> Diagnostics);

public interface ILegacyProfileExportService
{
    Result<LegacyProfileExportResult> Export(ModProfile profile, LegacyProfileExportOptions options);
}

public sealed class LegacyProfileExportService : ILegacyProfileExportService
{
    public Result<LegacyProfileExportResult> Export(ModProfile profile, LegacyProfileExportOptions options)
    {
        var metadata = profile.LegacyMetadata?.Rows.ToDictionary(row => row.Order) ?? [];
        var diagnostics = new List<string>
        {
            "legacy_export.game_omitted: Legacy text does not represent the game variant.",
            "legacy_export.arguments_omitted: Legacy text does not represent launch arguments.",
            "legacy_export.identity_omitted: Legacy text does not represent profile identity or timestamps."
        };
        var rows = profile.Mods.OrderBy(mod => mod.Order).Select(mod =>
        {
            metadata.TryGetValue(mod.Order, out var legacy);
            var name = string.IsNullOrWhiteSpace(legacy?.DisplayName) ? mod.PackageId.Value : legacy.DisplayName!;
            if (legacy is null) diagnostics.Add($"legacy_export.metadata_missing: {mod.PackageId.Value} uses its package ID as its display name and has no category or tags.");
            var source = mod.WorkshopId is null ? "Unknown" : options.WorkshopIdStyle == LegacyWorkshopIdStyle.Url ? $"https://steamcommunity.com/sharedfiles/filedetails/?id={mod.WorkshopId.Value.Value}" : mod.WorkshopId.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var category = options.IncludeCategories ? legacy?.Category : null;
            var tags = options.IncludeTags ? legacy?.Tags ?? [] : [];
            if (name.ContainsAny('\t', '\r', '\n') || mod.PackageId.Value.ContainsAny('\t', '\r', '\n') || tags.Any(tag => tag.ContainsAny(';', '\t', '\r', '\n')))
                diagnostics.Add($"legacy_export.lossy_characters: {mod.PackageId.Value} contains characters the legacy format cannot escape.");
            return new Row(name, mod.PackageId.Value, source, category, tags);
        }).ToArray();

        var builder = new System.Text.StringBuilder();
        if (options.IncludeCategories && rows.Any(row => !string.IsNullOrWhiteSpace(row.Category)))
        {
            foreach (var group in rows.GroupBy(row => row.Category ?? string.Empty))
            {
                builder.Append(group.Key).Append(" (").Append(group.Count()).AppendLine("):");
                foreach (var row in group) AppendRow(builder, row, true);
                builder.AppendLine();
            }
        }
        else foreach (var row in rows) AppendRow(builder, row, false);
        return Result<LegacyProfileExportResult>.Success(new(builder.ToString(), diagnostics.Distinct().ToArray()));
    }

    private static void AppendRow(System.Text.StringBuilder builder, Row row, bool indented) => builder.Append(indented ? "\t" : string.Empty).Append(row.Name).Append(" \t").Append(row.PackageId).Append(" \t").Append(row.Source).Append('\t').Append(string.Join(';', row.Tags)).AppendLine();
    private sealed record Row(string Name, string PackageId, string Source, string? Category, IReadOnlyList<string> Tags);
}

file static class LegacyTextExtensions
{
    public static bool ContainsAny(this string value, params char[] characters) => value.IndexOfAny(characters) >= 0;
}
