namespace AAML.Infrastructure.Common.Configurations;

/// <summary>Replaces repeated values for one Unreal INI key while preserving unrelated text.</summary>
public static class UnrealIniUpdater
{
    public static string ReplaceValues(string contents, string section, string key, IEnumerable<string> values)
    {
        var newline = contents.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        var sectionHeader = $"[{section}]";
        var start = lines.FindIndex(line => line.Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(string.Empty);
            lines.Add(sectionHeader);
            start = lines.Count - 1;
        }
        var end = lines.FindIndex(start + 1, line => line.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        for (var index = end - 1; index > start; index--)
        {
            var separator = lines[index].IndexOf('=');
            if (separator >= 0 && lines[index][..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(index);
        }
        var snapshot = values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lines.InsertRange(start + 1, snapshot.Select(value => $"{key}={value}"));
        return string.Join(newline, lines).TrimEnd('\r', '\n') + newline;
    }
}
