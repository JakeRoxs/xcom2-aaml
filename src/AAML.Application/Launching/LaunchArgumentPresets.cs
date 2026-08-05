using AAML.Domain.Games;
using System.Security.Cryptography;
using System.Text;

namespace AAML.Application.Launching;

/// <summary>Describes one AAML-owned or imported launch-argument suggestion.</summary>
public sealed record LaunchArgumentPreset(
    string Id,
    string ArgumentTemplate,
    string FriendlyName,
    string Description,
    IReadOnlySet<GameVariant> ApplicableGames,
    bool RequiresValue,
    bool IsImported,
    bool IsAdvanced)
{
    /// <summary>Returns whether this preset is available for a game variant.</summary>
    public bool AppliesTo(GameVariant game) => ApplicableGames.Contains(game);

    /// <summary>Formats a concrete argument, or returns <see langword="null"/> when a required value is absent.</summary>
    public string? Format(string? value = null)
    {
        if (!RequiresValue) return ArgumentTemplate;
        return string.IsNullOrWhiteSpace(value) ? null : ArgumentTemplate + value.Trim();
    }

    /// <summary>Returns whether a concrete argument is equivalent to this preset.</summary>
    public bool Matches(string argument)
    {
        if (RequiresValue) return argument.StartsWith(ArgumentTemplate, StringComparison.OrdinalIgnoreCase) && argument.Length > ArgumentTemplate.Length;
        if (Id == "no-red-screens")
            return argument.Equals("-noRedScreens", StringComparison.OrdinalIgnoreCase) || argument.Equals("-noRedscreens", StringComparison.OrdinalIgnoreCase);
        return argument.Equals(ArgumentTemplate, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Raw versioned suggestions read from legacy migration metadata.</summary>
public sealed record LegacyLaunchArgumentSuggestionDocument(
    int SchemaVersion,
    string? SourceSha256,
    bool SourcePreserved,
    IReadOnlyList<string> Arguments);

/// <summary>One non-fatal preset catalog diagnostic.</summary>
public sealed record LaunchArgumentPresetDiagnostic(string Code, string Message);

/// <summary>Result of reading optional legacy launch-argument suggestions.</summary>
public sealed record LegacyLaunchArgumentSuggestionReadResult(
    LegacyLaunchArgumentSuggestionDocument? Document,
    IReadOnlyList<LaunchArgumentPresetDiagnostic> Diagnostics);

/// <summary>Reads optional legacy launch-argument suggestions without activating them.</summary>
public interface ILegacyLaunchArgumentSuggestionRepository
{
    /// <summary>Reads the optional versioned migration report.</summary>
    Task<LegacyLaunchArgumentSuggestionReadResult> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>One merged launch preset catalog and its recoverable diagnostics.</summary>
public sealed record LaunchArgumentPresetCatalog(
    IReadOnlyList<LaunchArgumentPreset> Presets,
    IReadOnlyList<LaunchArgumentPresetDiagnostic> Diagnostics);

/// <summary>Provides AAML presets augmented by validated custom legacy suggestions.</summary>
public interface ILaunchArgumentPresetService
{
    /// <summary>Gets the built-in catalog without requiring migration metadata.</summary>
    IReadOnlyList<LaunchArgumentPreset> BuiltIns { get; }

    /// <summary>Loads the built-ins and optional imported suggestions.</summary>
    Task<LaunchArgumentPresetCatalog> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>Merges the immutable AAML catalog with non-activating legacy suggestions.</summary>
public sealed class LaunchArgumentPresetService(ILegacyLaunchArgumentSuggestionRepository repository) : ILaunchArgumentPresetService
{
    private static readonly IReadOnlySet<GameVariant> AllGames = Enum.GetValues<GameVariant>().ToHashSet();
    private static readonly IReadOnlySet<GameVariant> ConsoleGames = Enum.GetValues<GameVariant>()
        .Where(game => game != GameVariant.XCom2WarOfTheChosenChallengeMode)
        .ToHashSet();

    private static readonly IReadOnlyList<LaunchArgumentPreset> StandardPresets =
    [
        BuiltIn("review", "-review", "Normal main menu", "Use the normal player-facing main menu.", AllGames),
        BuiltIn("no-red-screens", "-noRedScreens", "Hide red screens", "Suppress developer error dialogs. This can hide diagnostic information.", AllGames),
        BuiltIn("log", "-log", "Show log console", "Open the live game log console while the game runs.", AllGames),
        BuiltIn("crash-dump-watcher", "-crashDumpWatcher", "Crash dump watcher", "Legacy crash-dump watcher option; its behavior is undocumented.", AllGames, advanced: true),
        BuiltIn("skip-startup-movies", "-noStartUpMovies", "Skip startup movies", "Skip startup and intro movies.", AllGames),
        BuiltIn("language", "-language=", "Set language", "Force a game locale, for example INT.", AllGames, requiresValue: true),
        BuiltIn("allow-console", "-allowConsole", "Enable console", "Enable the in-game developer console.", ConsoleGames),
        BuiltIn("auto-debug", "-autoDebug", "Autodebug", "Legacy Unreal debugger startup option; behavior is not qualified by AAML.", AllGames, advanced: true),
        BuiltIn("no-seek-free-loading", "-noSeekFreeLoading", "No seek-free loading", "Legacy Unreal debugger loading option; behavior is not qualified by AAML.", AllGames, advanced: true),
        BuiltIn("regenerate-inis", "-regenerateinis", "Regenerate INI files", "Resets generated game configuration, including user options, on the next launch.", AllGames, advanced: true)
    ];

    /// <inheritdoc />
    public IReadOnlyList<LaunchArgumentPreset> BuiltIns => StandardPresets;

    /// <inheritdoc />
    public async Task<LaunchArgumentPresetCatalog> LoadAsync(CancellationToken cancellationToken)
    {
        var read = await repository.LoadAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = read.Diagnostics.ToList();
        if (read.Document is not { } document) return new(StandardPresets, diagnostics);
        if (document.SchemaVersion != 1)
        {
            diagnostics.Add(new("launch_presets.unsupported_report_schema", $"Legacy migration report schema {document.SchemaVersion} is not supported; custom suggestions were ignored."));
            return new(StandardPresets, diagnostics);
        }
        if (!document.SourcePreserved || !IsSha256(document.SourceSha256))
        {
            diagnostics.Add(new("launch_presets.invalid_report_source", "Legacy migration report provenance was invalid; custom suggestions were ignored."));
            return new(StandardPresets, diagnostics);
        }

        var presets = StandardPresets.ToList();
        var seen = new HashSet<string>(StandardPresets.Select(preset => EquivalenceKey(preset.ArgumentTemplate)), StringComparer.OrdinalIgnoreCase);
        foreach (var suggestion in document.Arguments)
        {
            var argument = suggestion.Trim();
            if (!IsValidSuggestion(argument))
            {
                diagnostics.Add(new("launch_presets.invalid_custom_suggestion", "A malformed legacy quick-toggle suggestion was ignored."));
                continue;
            }
            if (!seen.Add(EquivalenceKey(argument))) continue;
            presets.Add(new(
                $"imported-{StableId(argument)}",
                argument,
                argument,
                "Custom suggestion imported from the preserved legacy launcher settings.",
                AllGames,
                false,
                true,
                true));
        }
        return new(presets, diagnostics);
    }

    private static LaunchArgumentPreset BuiltIn(string id, string argument, string name, string description, IReadOnlySet<GameVariant> games, bool requiresValue = false, bool advanced = false) =>
        new(id, argument, name, description, games, requiresValue, false, advanced);

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));

    private static bool IsValidSuggestion(string value) => value.Length > 1 && value[0] == '-' && !value.Any(char.IsControl) && !value.Any(char.IsWhiteSpace);

    private static string EquivalenceKey(string value) =>
        value.Equals("-noRedscreens", StringComparison.OrdinalIgnoreCase) ? "-noRedScreens" : value;

    private static string StableId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant())))[..12].ToLowerInvariant();
}
