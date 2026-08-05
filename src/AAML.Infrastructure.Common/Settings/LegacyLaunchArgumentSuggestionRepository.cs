using AAML.Application.Launching;
using Newtonsoft.Json.Linq;

namespace AAML.Infrastructure.Common.Settings;

/// <summary>Reads custom launch suggestions from AAML's preserved v1 legacy migration report.</summary>
public sealed class LegacyLaunchArgumentSuggestionRepository(string path) : ILegacyLaunchArgumentSuggestionRepository
{
    /// <inheritdoc />
    public async Task<LegacyLaunchArgumentSuggestionReadResult> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new(null, []);
        try
        {
            var root = JObject.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            var diagnostics = new List<LaunchArgumentPresetDiagnostic>();
            var arguments = new List<string>();
            if (root["quickToggleArguments"] is JArray array)
            {
                foreach (var token in array)
                {
                    if (token?.Type == JTokenType.String && token.Value<string>() is { } value) arguments.Add(value);
                    else diagnostics.Add(new("launch_presets.malformed_report_entry", "A non-string legacy quick-toggle suggestion was ignored."));
                }
            }
            else if (root["quickToggleArguments"] is not null)
            {
                diagnostics.Add(new("launch_presets.malformed_report_entries", "Legacy quick-toggle suggestions were not an array and were ignored."));
            }

            var document = new LegacyLaunchArgumentSuggestionDocument(
                root.Value<int?>("schemaVersion") ?? 0,
                root.Value<string>("sourceSha256"),
                root.Value<bool?>("sourcePreserved") ?? false,
                arguments);
            return new(document, diagnostics);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is Newtonsoft.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return new(null, [new("launch_presets.report_read_failed", $"Legacy launch suggestions were unavailable: {exception.Message}")]);
        }
    }
}
