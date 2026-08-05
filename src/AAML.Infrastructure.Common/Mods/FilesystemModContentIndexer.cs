using System.Text.RegularExpressions;
using AAML.Application.Common;
using AAML.Application.Mods.Conflicts;
using AAML.Domain.Mods;

namespace AAML.Infrastructure.Common.Mods;

/// <summary>Builds deterministic physical content manifests without following reparse points.</summary>
public sealed partial class FilesystemModContentIndexer : IModContentIndexer
{
    public async Task<Result<ModContentManifest>> IndexAsync(ModInstallation installation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = installation.Key.LocationIdentity;
            if (!Directory.Exists(root))
                return Result<ModContentManifest>.Failure(new Error("conflicts.mod_missing", $"Mod content directory does not exist: {root}", ErrorKind.NotFound));

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
            var paths = Directory.EnumerateFiles(root, "*", options).Order(StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal).ToArray();
            var files = new List<ModFileFact>(paths.Length);
            var overrides = new List<ModOverrideFact>();
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                files.Add(new ModFileFact(installation.Key, relative));
                if (relative.StartsWith("Config/", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).Equals("XComEngine.ini", StringComparison.OrdinalIgnoreCase))
                    await ReadClassOverridesAsync(path, relative, installation, overrides, cancellationToken).ConfigureAwait(false);
                else if (relative.StartsWith("Src/", StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".uc", StringComparison.OrdinalIgnoreCase) && !relative.StartsWith("Src/XComGame/", StringComparison.OrdinalIgnoreCase))
                    await ReadListenersAsync(path, relative, installation, overrides, cancellationToken).ConfigureAwait(false);
            }

            return Result<ModContentManifest>.Success(new ModContentManifest(installation.Key, installation.PackageId, files, overrides));
        }
        catch (OperationCanceledException)
        {
            return Result<ModContentManifest>.Failure(new Error("conflicts.cancelled", "Conflict indexing was cancelled.", ErrorKind.Cancelled));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Result<ModContentManifest>.Failure(new Error("conflicts.index_failed", exception.Message, ErrorKind.Io));
        }
    }

    private static async Task ReadClassOverridesAsync(string path, string relative, ModInstallation installation, List<ModOverrideFact> target, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = ClassOverrideRegex().Match(WhitespaceRegex().Replace(lines[index], string.Empty));
            if (match.Success)
                target.Add(new ModOverrideFact(installation.Key, installation.PackageId, ModOverrideKind.Class, match.Groups["base"].Value, match.Groups["replacement"].Value, relative, index + 1, lines[index]));
        }
    }

    private static async Task ReadListenersAsync(string path, string relative, ModInstallation installation, List<ModOverrideFact> target, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = ScreenClassRegex().Match(lines[index]);
            if (match.Success && !match.Groups["base"].Value.Equals("none", StringComparison.OrdinalIgnoreCase))
                target.Add(new ModOverrideFact(installation.Key, installation.PackageId, ModOverrideKind.UiScreenListener, match.Groups["base"].Value, Path.GetFileNameWithoutExtension(path), relative, index + 1, lines[index]));
        }
    }

    [GeneratedRegex("""^[+]?ModClassOverrides=\(BaseGameClass="(?<base>[^"]+)",ModClass="(?<replacement>[^"]+)"\)""")]
    private static partial Regex ClassOverrideRegex();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
    [GeneratedRegex(@"^\s*ScreenClass\s*=\s*(?:class')?(?<base>[a-z_][a-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenClassRegex();
}
