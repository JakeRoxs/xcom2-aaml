using System.Text.RegularExpressions;
using AAML.Application.Common;
using AAML.Application.Profiles;

namespace AAML.Infrastructure.Common.Compatibility.Profiles;

/// <summary>A mod entry parsed from a legacy profile.</summary>
public sealed record LegacyProfileEntry(string Name, string ModId, ulong SourceId, string Category, IReadOnlyList<string> Tags);

/// <summary>Parses legacy AML profile text.</summary>
public static partial class LegacyProfileCodec
{
    /// <summary>Parses all profile rows accepted by the legacy importer.</summary>
    public static IReadOnlyList<LegacyProfileEntry> Parse(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var entries = new List<LegacyProfileEntry>();
        var category = string.Empty;
        using var reader = new StringReader(contents);
        while (reader.ReadLine() is { } line)
        {
            var categoryMatch = CategoryRegex().Match(line);
            if (categoryMatch.Success)
            {
                category = categoryMatch.Groups["category"].Value;
            }

            var modMatch = ModEntryRegex().Match(line);
            if (!modMatch.Success)
            {
                continue;
            }

            entries.Add(new LegacyProfileEntry(
                modMatch.Groups["name"].Value,
                modMatch.Groups["id"].Value,
                ulong.Parse(modMatch.Groups["sourceID"].Value),
                category,
                modMatch.Groups["tags"].Value.Split(';').Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray()));
        }

        return entries;
    }

    internal static bool IsCategory(string line) => CategoryRegex().IsMatch(line);

    internal static LegacyProfileEntry? TryParseEntry(string line)
    {
        var match = ModEntryRegex().Match(line);
        return !match.Success
            ? null
            : new LegacyProfileEntry(
                match.Groups["name"].Value,
                match.Groups["id"].Value,
                ulong.Parse(match.Groups["sourceID"].Value),
                string.Empty,
                match.Groups["tags"].Value.Split(';').Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray());
    }

    [GeneratedRegex(@"^(?<category>.*?)\s\(\d*\):$")]
    internal static partial Regex CategoryRegex();

    [GeneratedRegex(@"^\s*(?<name>.*?)[ ]*\t(?<id>.*?)[ ]*\t(?:.*=)?(?<sourceID>\d+)([ ]*\t(?<tags>.*?))?$")]
    private static partial Regex ModEntryRegex();
}

public sealed partial class LegacyProfileParser : ILegacyProfileParser
{
    public Result<LegacyProfileParseResult> Parse(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var entries = new List<AAML.Application.Profiles.LegacyProfileEntry>();
        var diagnostics = new List<string>();
        using var reader = new StringReader(contents);
        var lineNumber = 0;
        string? category = null;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (LegacyProfileCodec.IsCategory(line)) { category = LegacyProfileCodec.CategoryRegex().Match(line).Groups["category"].Value; continue; }
            var match = ImportEntryRegex().Match(line);
            if (!match.Success)
            {
                diagnostics.Add($"Line {lineNumber} was skipped because it is not an importable Workshop mod.");
                continue;
            }
            var hasWorkshopId = ulong.TryParse(match.Groups["sourceID"].Value, out var workshopId);
            entries.Add(new AAML.Application.Profiles.LegacyProfileEntry(
                hasWorkshopId ? AAML.Domain.Mods.ModSource.SteamWorkshop : AAML.Domain.Mods.ModSource.Manual,
                match.Groups["id"].Value.Trim(),
                hasWorkshopId ? workshopId : null,
                lineNumber,
                match.Groups["name"].Value.Trim(),
                category,
                match.Groups["tags"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                line));
        }
        return Result<LegacyProfileParseResult>.Success(new LegacyProfileParseResult(entries, diagnostics));
    }

    [GeneratedRegex(@"^\s*(?<name>.*?)[ ]*\t(?<id>.*?)[ ]*\t(?:(?:.*=)?(?<sourceID>\d+)|Unknown)([ ]*\t(?<tags>.*?))?$")]
    private static partial Regex ImportEntryRegex();
}
