namespace AAML.Infrastructure.Common.Steam;

public sealed record ValveKeyValueEntry(string Key, string? Value, IReadOnlyList<ValveKeyValueEntry> Children, int Line);

/// <summary>Parses Valve KeyValues text used by Steam library and application manifests.</summary>
public sealed class ValveKeyValueParser
{
    private readonly string text;
    private int position;
    private int line = 1;

    private ValveKeyValueParser(string text) => this.text = text;

    public static IReadOnlyList<ValveKeyValueEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new ValveKeyValueParser(text);
        var entries = parser.ParseEntries(expectClosingBrace: false);
        parser.SkipWhitespaceAndComments();
        if (!parser.End) parser.Error("Trailing tokens are not valid KeyValues.");
        return entries;
    }

    private List<ValveKeyValueEntry> ParseEntries(bool expectClosingBrace)
    {
        var entries = new List<ValveKeyValueEntry>();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (End)
            {
                if (expectClosingBrace) Error("Missing closing brace.");
                return entries;
            }
            if (Current == '}')
            {
                if (!expectClosingBrace) Error("Unexpected closing brace.");
                position++;
                return entries;
            }
            var keyLine = line;
            var key = ReadString();
            SkipWhitespaceAndComments();
            if (End) Error("Key has no value.");
            if (Current == '{')
            {
                position++;
                entries.Add(new ValveKeyValueEntry(key, null, ParseEntries(true), keyLine));
            }
            else entries.Add(new ValveKeyValueEntry(key, ReadString(), [], keyLine));
        }
    }

    private string ReadString()
    {
        SkipWhitespaceAndComments();
        if (End) Error("Expected a quoted or bare token.");
        if (Current != '"')
        {
            var start = position;
            while (!End && !char.IsWhiteSpace(Current) && Current is not ('{' or '}')) position++;
            if (position == start) Error("Expected a token.");
            return text[start..position];
        }
        position++;
        var result = new System.Text.StringBuilder();
        while (!End)
        {
            var current = text[position++];
            if (current == '"') return result.ToString();
            if (current == '\\')
            {
                if (End) Error("Unterminated escape sequence.");
                var escaped = text[position++];
                result.Append(escaped is '"' or '\\' ? escaped : '\\').Append(escaped is '"' or '\\' ? string.Empty : escaped);
                continue;
            }
            if (current == '\n') line++;
            result.Append(current);
        }
        Error("Unterminated string.");
        return string.Empty;
    }

    private void SkipWhitespaceAndComments()
    {
        while (!End)
        {
            if (char.IsWhiteSpace(Current))
            {
                if (Current == '\n') line++;
                position++;
                continue;
            }
            if (Current == '/' && Peek() == '/')
            {
                position += 2;
                while (!End && Current != '\n') position++;
                continue;
            }
            break;
        }
    }

    private bool End => position >= text.Length;
    private char Current => text[position];
    private char Peek() => position + 1 < text.Length ? text[position + 1] : '\0';
    private void Error(string message) => throw new FormatException($"VDF line {line}: {message}");
}
