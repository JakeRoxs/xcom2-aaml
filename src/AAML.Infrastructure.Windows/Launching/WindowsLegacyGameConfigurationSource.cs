using System.Security.Cryptography;
using System.Text;
using AAML.Application.Common;
using AAML.Application.Configurations;
using AAML.Application.Ports;
using AAML.Domain.Games;

namespace AAML.Infrastructure.Windows.Launching;

public sealed class WindowsLegacyGameConfigurationSource : ILegacyGameConfigurationSource
{
    private readonly IAtomicTextWriter writer;
    private readonly string documentsDirectory;

    public WindowsLegacyGameConfigurationSource(IAtomicTextWriter writer) : this(writer, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) { }
    internal WindowsLegacyGameConfigurationSource(IAtomicTextWriter writer, string documentsDirectory) { this.writer = writer; this.documentsDirectory = documentsDirectory; }

    public async Task<Result<IReadOnlyList<ActiveModSource>>> ReadActiveModsAsync(GameVariant variant, string? installationLocation, CancellationToken cancellationToken)
    {
        var paths = Resolve(variant, installationLocation);
        if (!paths.IsSuccess) return Result<IReadOnlyList<ActiveModSource>>.Failure(paths.Error!);
        try
        {
            var sources = new List<ActiveModSource>();
            var generatedExists = File.Exists(paths.Value!.GeneratedModOptions);
            sources.Add(new(paths.Value.GeneratedModOptions, generatedExists ? await File.ReadAllTextAsync(paths.Value.GeneratedModOptions, cancellationToken).ConfigureAwait(false) : string.Empty, true, generatedExists));
            if (!string.IsNullOrWhiteSpace(paths.Value.DefaultModOptions))
            {
                var defaultExists = File.Exists(paths.Value.DefaultModOptions);
                sources.Add(new(paths.Value.DefaultModOptions, defaultExists ? await File.ReadAllTextAsync(paths.Value.DefaultModOptions, cancellationToken).ConfigureAwait(false) : string.Empty, false, defaultExists));
            }
            return Result<IReadOnlyList<ActiveModSource>>.Success(sources);
        }
        catch (OperationCanceledException) { return Result<IReadOnlyList<ActiveModSource>>.Failure(new Error("active_mods.read_cancelled", "Active-mod import was cancelled.", ErrorKind.Cancelled)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Result<IReadOnlyList<ActiveModSource>>.Failure(new Error("active_mods.read_failed", exception.Message, ErrorKind.Io)); }
    }

    public async Task<Result<ObsoleteOverridePreview>> PreviewOverrideCleanupAsync(GameVariant variant, CancellationToken cancellationToken)
    {
        var paths = Resolve(variant, null);
        if (!paths.IsSuccess) return Result<ObsoleteOverridePreview>.Failure(paths.Error!);
        var path = paths.Value!.Engine;
        try
        {
            var contents = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : string.Empty;
            var cleaned = RemoveOverrides(contents);
            var fingerprint = Fingerprint(contents);
            var report = $"Obsolete ModClassOverrides cleanup preview\nVariant: {variant}\nPath: {path}\nSource SHA-256: {fingerprint}\nRows removed: {cleaned.Removed}\nBackup on apply: {(cleaned.Removed > 0 ? "yes" : "no")}";
            return Result<ObsoleteOverridePreview>.Success(new(variant, path, fingerprint, cleaned.Removed, cleaned.Contents, report));
        }
        catch (OperationCanceledException) { return Result<ObsoleteOverridePreview>.Failure(new Error("override_cleanup.cancelled", "Override cleanup was cancelled.", ErrorKind.Cancelled)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Result<ObsoleteOverridePreview>.Failure(new Error("override_cleanup.read_failed", exception.Message, ErrorKind.Io)); }
    }

    public async Task<Result> ApplyOverrideCleanupAsync(ObsoleteOverridePreview preview, CancellationToken cancellationToken)
    {
        try
        {
            var current = File.Exists(preview.Path) ? await File.ReadAllTextAsync(preview.Path, cancellationToken).ConfigureAwait(false) : string.Empty;
            if (!Fingerprint(current).Equals(preview.SourceFingerprint, StringComparison.Ordinal)) return Result.Failure(new Error("override_cleanup.preview_stale", "XComEngine.ini changed after preview.", ErrorKind.Conflict));
            return preview.RemovedRows == 0 ? Result.Success() : await writer.WriteAsync(preview.Path, preview.RevisedContents, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return Result.Failure(new Error("override_cleanup.cancelled", "Override cleanup was cancelled.", ErrorKind.Cancelled)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Result.Failure(new Error("override_cleanup.read_failed", exception.Message, ErrorKind.Io)); }
    }

    private Result<Paths> Resolve(GameVariant variant, string? installation)
    {
        if (variant == GameVariant.XCom2WarOfTheChosenChallengeMode) return Result<Paths>.Failure(new Error("configuration.variant_unsupported", "Challenge mode is not supported.", ErrorKind.Validation));
        var folder = variant switch { GameVariant.XCom2 => "XCOM2", GameVariant.XCom2WarOfTheChosen => "XCOM2 War of the Chosen", GameVariant.ChimeraSquad => "XCOM Chimera Squad", _ => throw new ArgumentOutOfRangeException(nameof(variant)) };
        var user = Path.Combine(documentsDirectory, "My Games", folder, "XComGame", "Config");
        var defaults = installation is null ? string.Empty : variant == GameVariant.XCom2WarOfTheChosen ? Path.Combine(installation, "XCom2-WarOfTheChosen", "XComGame", "Config", "DefaultModOptions.ini") : Path.Combine(installation, "XComGame", "Config", "DefaultModOptions.ini");
        return Result<Paths>.Success(new(Path.Combine(user, "XComModOptions.ini"), defaults, Path.Combine(user, "XComEngine.ini")));
    }

    private static (string Contents, int Removed) RemoveOverrides(string contents)
    {
        var newline = contents.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var finalNewline = contents.EndsWith('\n') || contents.EndsWith('\r');
        var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        var inSection = false; var removed = 0;
        for (var index = 0; index < lines.Count;)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) { inSection = trimmed.Equals("[Engine.Engine]", StringComparison.OrdinalIgnoreCase); index++; continue; }
            var separator = lines[index].IndexOf('=');
            var key = separator < 0 ? string.Empty : lines[index][..separator].Trim().TrimStart('+', '.', '-', '!');
            if (!inSection || !key.Equals("ModClassOverrides", StringComparison.OrdinalIgnoreCase) || lines[index].TrimStart().StartsWith(';') || lines[index].TrimStart().StartsWith('#')) { index++; continue; }
            lines.RemoveAt(index); removed++;
            while (index < lines.Count && (lines[index].StartsWith(' ') || lines[index].StartsWith('\t')) && !lines[index].Contains('=')) lines.RemoveAt(index);
        }
        var revised = string.Join(newline, lines).TrimEnd('\r', '\n') + (finalNewline ? newline : string.Empty);
        return (revised, removed);
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record Paths(string GeneratedModOptions, string DefaultModOptions, string Engine);
}
