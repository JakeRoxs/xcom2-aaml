namespace AAML.Infrastructure.Common.Compatibility.Mods;

/// <summary>Parses XComMod descriptors without legacy global settings or filesystem side effects.</summary>
public static class LegacyModMetadataParser
{
    private static readonly HashSet<string> ValidKeys =
    [
        "publishedfileid", "title", "category", "description", "tags", "contentimage", "requiresxpack"
    ];

    /// <summary>Parses a descriptor using legacy continuation and default rules.</summary>
    public static LegacyModMetadata Parse(string contents, bool useSpecifiedCategory = true, Func<string, bool>? contentImageExists = null)
    {
        ArgumentNullException.ThrowIfNull(contents);

        using var reader = new StringReader(contents);
        _ = reader.ReadLine();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentKey = null;

        while (reader.ReadLine() is { } line)
        {
            if (currentKey is null || line.Contains('=', StringComparison.Ordinal))
            {
                var pair = line.Split(['='], 2);
                var newKey = pair[0].Trim().ToLowerInvariant();
                if (currentKey is null || ValidKeys.Contains(newKey))
                {
                    currentKey = newKey;
                    if (pair.Length < 2)
                    {
                        continue;
                    }

                    values[currentKey] = pair[1];
                }
                else
                {
                    values[currentKey] += "\r\n" + line;
                }
            }
            else if (!string.IsNullOrEmpty(values[currentKey]))
            {
                values[currentKey] += "\r\n" + line;
            }
        }

        var publishedFileId = values.TryGetValue("publishedfileid", out var id) && long.TryParse(id, out var parsedId) ? parsedId : -1;
        var category = useSpecifiedCategory && values.TryGetValue("category", out var specifiedCategory) && specifiedCategory.Length > 0
            ? specifiedCategory
            : "Unsorted";
        var contentImage = values.TryGetValue("contentimage", out var image) && image.Trim().Length > 0 && contentImageExists?.Invoke(image.Trim()) == true
            ? image
            : "ModPreview.jpg";

        return new LegacyModMetadata(
            publishedFileId,
            values.GetValueOrDefault("title"),
            category,
            values.GetValueOrDefault("description", string.Empty),
            values.GetValueOrDefault("tags", string.Empty),
            values.TryGetValue("requiresxpack", out var requires) && requires.Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
            contentImage);
    }
}
