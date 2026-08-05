namespace AAML.Infrastructure.Common.Compatibility.Ini;

/// <summary>Represents the ordered sections, keys, and duplicate values parsed by the legacy INI reader.</summary>
public sealed class LegacyIniDocument
{
    private readonly Dictionary<string, Dictionary<string, List<string>>> entries = [];

    /// <summary>Gets the parsed entries.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, List<string>>> Entries => entries;

    /// <summary>Parses INI text using the legacy launcher rules.</summary>
    /// <param name="contents">INI text.</param>
    /// <returns>A parsed document.</returns>
    public static LegacyIniDocument Parse(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var document = new LegacyIniDocument();
        using var reader = new StringReader(contents);
        var currentSection = string.Empty;

        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator];
            if (key.StartsWith(';'))
            {
                continue;
            }

            var value = line[(separator + 1)..];
            while (value.Length > 2 && value.EndsWith("\\\\", StringComparison.Ordinal))
            {
                value = value[..^2] + "\n" + reader.ReadLine();
            }

            document.Add(currentSection, key.TrimEnd(), value.Replace("%GAME%", "XCom", StringComparison.Ordinal).TrimStart());
        }

        return document;
    }

    /// <summary>Gets values for a section and key.</summary>
    public IReadOnlyList<string> Get(string section, string key) =>
        entries.TryGetValue(section, out var sectionEntries) && sectionEntries.TryGetValue(key, out var values)
            ? values
            : [];

    /// <summary>Applies a parsed default-config overlay using legacy Unreal operator semantics.</summary>
    public void ApplyOverlay(LegacyIniDocument overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        foreach (var (section, sectionEntries) in overlay.entries)
        {
            foreach (var (operatorKey, values) in sectionEntries)
            {
                var operation = operatorKey[0];
                var key = operation is '+' or '.' or '-' or '!' ? operatorKey[1..] : operatorKey;
                switch (operation)
                {
                    case '+':
                    case '.':
                        foreach (var value in values)
                        {
                            Add(section, key, value);
                        }
                        break;
                    case '-':
                        foreach (var value in values)
                        {
                            Remove(section, key, value);
                        }
                        break;
                    case '!':
                        Set(section, key, []);
                        break;
                    case ';':
                        break;
                    default:
                        foreach (var value in values)
                        {
                            Set(section, key, [value]);
                        }
                        break;
                }
            }
        }
    }

    private void Add(string section, string key, string value)
    {
        if (!entries.TryGetValue(section, out var sectionEntries))
        {
            sectionEntries = [];
            entries.Add(section, sectionEntries);
        }

        if (!sectionEntries.TryGetValue(key, out var values))
        {
            values = [];
            sectionEntries.Add(key, values);
        }

        values.Add(value);
    }

    private void Set(string section, string key, List<string> values)
    {
        if (!entries.TryGetValue(section, out var sectionEntries))
        {
            sectionEntries = [];
            entries.Add(section, sectionEntries);
        }

        sectionEntries[key] = values;
    }

    private void Remove(string section, string key, string value)
    {
        if (entries.TryGetValue(section, out var sectionEntries) && sectionEntries.TryGetValue(key, out var values))
        {
            values.RemoveAll(candidate => candidate == value);
        }
    }
}
