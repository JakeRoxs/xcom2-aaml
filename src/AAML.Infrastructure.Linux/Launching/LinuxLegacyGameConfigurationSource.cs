using System.Security.Cryptography;
using System.Text;
using AAML.Application.Common;
using AAML.Application.Configurations;
using AAML.Domain.Games;

namespace AAML.Infrastructure.Linux.Launching;

/// <summary>Reads generated XCOM 2 roots from the exact supported Steam Proton prefix without modifying it.</summary>
public sealed class LinuxLegacyGameConfigurationSource : ILegacyGameConfigurationSource
{
    private static Error Unsupported() => new("legacy_configuration.unsupported_platform", "Active-mod and override migration from game files is available on Windows only.", ErrorKind.Validation);

    public Task<Result<IReadOnlyList<ActiveModSource>>> ReadActiveModsAsync(GameVariant variant, string? installationLocation, CancellationToken cancellationToken) =>
        Task.FromResult(Result<IReadOnlyList<ActiveModSource>>.Failure(Unsupported()));

    public async Task<Result<ExistingModRootPreview>> ReadModRootsAsync(GameVariant variant, string? installationLocation, IReadOnlyList<string> configuredRoots, CancellationToken cancellationToken)
    {
        if (!GameModRootPolicy.SupportsLinuxProton(variant))
            return Result<ExistingModRootPreview>.Failure(new Error("mod_roots.variant_unsupported", "Linux Proton ModRootDirs migration supports XCOM 2 Vanilla and War of the Chosen only; Chimera Squad is not supported.", ErrorKind.Validation));
        if (string.IsNullOrWhiteSpace(installationLocation))
            return Result<ExistingModRootPreview>.Failure(new Error("mod_roots.installation_required", "Configure the selected game installation before previewing existing roots.", ErrorKind.Validation));
        var layout = LinuxSteamGameLayout.Resolve(installationLocation, variant);
        if (!layout.IsSuccess) return Result<ExistingModRootPreview>.Failure(layout.Error!);
        var sourcePath = Path.Combine(layout.Value!.ConfigurationDirectory, "XComEngine.ini");
        try
        {
            var contents = File.Exists(sourcePath) ? await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false) : string.Empty;
            var binary = Path.GetDirectoryName(layout.Value.TargetExecutablePath)!;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var configured = configuredRoots.Select(TryFullPath).Where(path => path is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
            var rows = ExistingModRootIniParser.Parse(contents, StringComparer.Ordinal).Select((item, index) => Classify(index, item.Value, item.Line, layout.Value, binary, seen, configured)).ToArray();
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents))).ToLowerInvariant();
            var behavior = $"Linux Steam/Proton only: generated prefix {sourcePath}; S: maps to {layout.Value.SteamAppsPath}, Z: maps to /, and relative roots resolve against {binary}. Chimera Squad is not supported.";
            var report = $"ModRootDirs migration preview\nVariant: {variant}\nSource: {sourcePath}\nSource preserved: yes\nSource SHA-256: {fingerprint}\nBehavior: {behavior}\n" + (rows.Length == 0 ? "No ModRootDirs entries found." : string.Join('\n', rows.Select(row => $"{row.Index + 1}. line {row.LineNumber} | {row.Resolution} | {row.RawValue} | {row.ResolvedPath ?? "-"}")));
            return Result<ExistingModRootPreview>.Success(new(variant, Path.GetFullPath(installationLocation), sourcePath, fingerprint, behavior, rows, report));
        }
        catch (OperationCanceledException) { return Result<ExistingModRootPreview>.Failure(new Error("mod_roots.cancelled", "Mod-root preview was cancelled.", ErrorKind.Cancelled)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        { return Result<ExistingModRootPreview>.Failure(new Error("mod_roots.read_failed", exception.Message, ErrorKind.Io)); }
    }

    public Task<Result<ObsoleteOverridePreview>> PreviewOverrideCleanupAsync(GameVariant variant, CancellationToken cancellationToken) => Task.FromResult(Result<ObsoleteOverridePreview>.Failure(Unsupported()));
    public Task<Result> ApplyOverrideCleanupAsync(ObsoleteOverridePreview preview, CancellationToken cancellationToken) => Task.FromResult(Result.Failure(Unsupported()));

    private static ExistingModRootRow Classify(int index, string? raw, int line, LinuxSteamGameLayout layout, string binary, HashSet<string> seen, HashSet<string> configured)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new(index, raw ?? string.Empty, null, line, ExistingModRootResolution.Malformed);
        try
        {
            var value = raw.Trim();
            if (value.Length >= 1 && (value[0] == '"' || value[^1] == '"'))
            {
                if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return new(index, raw, null, line, ExistingModRootResolution.Malformed);
                value = value[1..^1].Trim();
            }
            if (value.Length == 0 || value.IndexOf('\0') >= 0) return new(index, raw, null, line, ExistingModRootResolution.Malformed);
            var normalizedValue = NormalizePathValue(value);
            var relative = !normalizedValue.StartsWith('/') && !(normalizedValue.Length >= 2 && normalizedValue[1] == ':' && (normalizedValue[2] == '\\' || normalizedValue[2] == '/'));
            string candidate;
            if (relative) candidate = Path.GetFullPath(Path.Combine(binary, normalizedValue));
            else if (normalizedValue.StartsWith("S:", StringComparison.OrdinalIgnoreCase)) candidate = Path.GetFullPath(Path.Combine(layout.SteamAppsPath, normalizedValue[2..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            else if (normalizedValue.StartsWith("Z:", StringComparison.OrdinalIgnoreCase)) candidate = Path.GetFullPath("/" + normalizedValue[2..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            else if (normalizedValue.StartsWith('/')) candidate = Path.GetFullPath(normalizedValue);
            else return new(index, raw, null, line, ExistingModRootResolution.Malformed);
            if (relative && !IsContained(candidate, layout.GameInstallPath)) return new(index, raw, candidate, line, ExistingModRootResolution.OutsideRoot);
            if (!seen.Add(candidate)) return new(index, raw, candidate, line, ExistingModRootResolution.Duplicate);
            if (configured.Contains(candidate)) return new(index, raw, candidate, line, ExistingModRootResolution.AlreadyConfigured);
            if (!Directory.Exists(candidate)) return new(index, raw, candidate, line, ExistingModRootResolution.Missing);
            if (HasReparsePoint(candidate)) return new(index, raw, candidate, line, ExistingModRootResolution.Reparse);
            return new(index, raw, candidate, line, ExistingModRootResolution.Valid);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return new(index, raw, null, line, ExistingModRootResolution.Malformed); }
    }

    private static bool IsContained(string candidate, string root) { var relative = Path.GetRelativePath(root, candidate); return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative); }
    private static bool HasReparsePoint(string path) { for (var current = new DirectoryInfo(path); current is not null; current = current.Parent) if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true; return false; }
    private static string? TryFullPath(string path) { try { return Path.GetFullPath(NormalizePathValue(path)); } catch { return null; } }
    private static string NormalizePathValue(string value)
    {
        var normalized = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (normalized.Length > 1) normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalized;
    }
}
