using AAML.Application.Common;
using AAML.Domain.Games;

namespace AAML.Application.Configurations;

public sealed record ObsoleteOverridePreview(GameVariant Variant, string Path, string SourceFingerprint, int RemovedRows, string RevisedContents, string Report);
public enum ExistingModRootResolution { Valid, Missing, Duplicate, OutsideRoot, Reparse, Malformed, AlreadyConfigured }
public sealed record ExistingModRootRow(int Index, string RawValue, string? ResolvedPath, int LineNumber, ExistingModRootResolution Resolution);
public sealed record ExistingModRootPreview(GameVariant Variant, string InstallationLocation, string SourcePath, string SourceFingerprint, string PlatformBehavior, IReadOnlyList<ExistingModRootRow> Rows, string Report);
public sealed record LegacyGameConfigurationCapabilities(bool CanReadActiveMods, bool CanReadModRoots, bool CanCleanupOverrides, string Guidance)
{
    public static LegacyGameConfigurationCapabilities Windows { get; } = new(true, true, true, "Game-file migration is available for supported Windows variants.");
    public static LegacyGameConfigurationCapabilities Unavailable { get; } = new(false, false, false, "Automatic game-file migration is unavailable on this platform. Portable snapshot migration remains available.");
}

/// <summary>Parses the effective ModRootDirs values from Unreal array directives.</summary>
public static class ExistingModRootIniParser
{
    public static IReadOnlyList<(string? Value, int Line)> Parse(string contents, StringComparer comparer)
    {
        var rows = new List<(string?, int)>();
        var inSection = false;
        var lineNumber = 0;
        using var reader = new StringReader(contents);
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line.Equals("[Engine.DownloadableContentEnumerator]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

            var separator = line.IndexOf('=');
            var keyToken = (separator < 0 ? line : line[..separator]).Trim();
            var operation = keyToken.Length > 0 && keyToken[0] is '+' or '-' or '.' or '!' ? keyToken[0] : '\0';
            if (!keyToken.TrimStart('+', '-', '.', '!').Trim().Equals("ModRootDirs", StringComparison.OrdinalIgnoreCase)) continue;
            var value = separator < 0 ? null : line[(separator + 1)..].Trim();

            if (operation == '!')
            {
                rows.Clear();
                continue;
            }
            if (operation == '-')
            {
                if (value is not null) rows.RemoveAll(item => comparer.Equals(NormalizeDirectiveValue(item.Item1), NormalizeDirectiveValue(value)));
                continue;
            }
            rows.Add((value, lineNumber));
        }
        return rows;
    }

    private static string? NormalizeDirectiveValue(string? value)
    {
        var normalized = value?.Trim();
        return normalized is { Length: >= 2 } && normalized[0] == '"' && normalized[^1] == '"'
            ? normalized[1..^1].Trim()
            : normalized;
    }
}

public interface ILegacyGameConfigurationSource
{
    LegacyGameConfigurationCapabilities Capabilities => LegacyGameConfigurationCapabilities.Windows;
    bool SupportsActiveMods(GameVariant variant) => Capabilities.CanReadActiveMods;
    bool SupportsModRoots(GameVariant variant) => Capabilities.CanReadModRoots;
    bool SupportsOverrideCleanup(GameVariant variant) => Capabilities.CanCleanupOverrides;
    Task<Result<IReadOnlyList<ActiveModSource>>> ReadActiveModsAsync(GameVariant variant, string? installationLocation, CancellationToken cancellationToken);
    Task<Result<ExistingModRootPreview>> ReadModRootsAsync(GameVariant variant, string? installationLocation, IReadOnlyList<string> configuredRoots, CancellationToken cancellationToken);
    Task<Result<ObsoleteOverridePreview>> PreviewOverrideCleanupAsync(GameVariant variant, CancellationToken cancellationToken);
    Task<Result> ApplyOverrideCleanupAsync(ObsoleteOverridePreview preview, CancellationToken cancellationToken);
}

public sealed class UnavailableLegacyGameConfigurationSource : ILegacyGameConfigurationSource
{
    public LegacyGameConfigurationCapabilities Capabilities => LegacyGameConfigurationCapabilities.Unavailable;
    public bool SupportsActiveMods(GameVariant variant) => false;
    public bool SupportsModRoots(GameVariant variant) => false;
    public bool SupportsOverrideCleanup(GameVariant variant) => false;
    private static Error Unsupported() => new("legacy_configuration.unsupported_platform", "Automatic .NET Framework AML configuration discovery is available on Windows only.", ErrorKind.Validation);
    public Task<Result<IReadOnlyList<ActiveModSource>>> ReadActiveModsAsync(GameVariant variant, string? installationLocation, CancellationToken cancellationToken) => Task.FromResult(Result<IReadOnlyList<ActiveModSource>>.Failure(Unsupported()));
    public Task<Result<ExistingModRootPreview>> ReadModRootsAsync(GameVariant variant, string? installationLocation, IReadOnlyList<string> configuredRoots, CancellationToken cancellationToken) => Task.FromResult(Result<ExistingModRootPreview>.Failure(Unsupported()));
    public Task<Result<ObsoleteOverridePreview>> PreviewOverrideCleanupAsync(GameVariant variant, CancellationToken cancellationToken) => Task.FromResult(Result<ObsoleteOverridePreview>.Failure(Unsupported()));
    public Task<Result> ApplyOverrideCleanupAsync(ObsoleteOverridePreview preview, CancellationToken cancellationToken) => Task.FromResult(Result.Failure(Unsupported()));
}
