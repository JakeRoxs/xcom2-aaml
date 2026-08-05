using Newtonsoft.Json.Linq;

namespace AAML.Infrastructure.Common.Compatibility.Settings;

/// <summary>Reads command-line arguments from persisted legacy settings shapes.</summary>
public static class LegacySettingsArguments
{
    private static readonly string[] DefaultArguments = ["-review", "-noRedScreens"];

    /// <summary>Extracts arguments using the legacy migration rules.</summary>
    /// <param name="json">A persisted settings JSON document.</param>
    /// <returns>The argument sequence that the legacy launcher would use.</returns>
    public static IReadOnlyList<string> Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var root = JObject.Parse(json);
        if (root["ArgumentList"] is JArray current)
        {
            return current.Values<string>().Where(value => value is not null).Cast<string>().ToArray();
        }

        return root["Arguments"] switch
        {
            JObject => DefaultArguments,
            JValue { Type: JTokenType.String } value => SplitLegacyString(value.Value<string>()),
            _ => DefaultArguments
        };
    }

    private static IReadOnlyList<string> SplitLegacyString(string? value) =>
        string.IsNullOrEmpty(value)
            ? DefaultArguments
            : value.Split(' ')
                .Where(argument => !string.IsNullOrWhiteSpace(argument))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
}
