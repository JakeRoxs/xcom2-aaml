using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Mods;
using AAML.Infrastructure.Common.Compatibility.Mods;

namespace AAML.Infrastructure.Common.Mods;

/// <summary>Discovers mod descriptors beneath explicit configured roots without mutating files.</summary>
public sealed class FilesystemModCatalogSource(IPathSemantics pathSemantics) : IModCatalogSource
{
    public Task<Result<IReadOnlyList<ModInstallation>>> DiscoverAsync(
        IReadOnlyList<string> roots,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken) => cancellationToken.IsCancellationRequested
        ? Task.FromResult(Result<IReadOnlyList<ModInstallation>>.Failure(new Error("catalog.cancelled", "Mod discovery was cancelled.", ErrorKind.Cancelled)))
        : Task.Run(() => Discover(roots, progress, cancellationToken), cancellationToken);

    private Result<IReadOnlyList<ModInstallation>> Discover(IReadOnlyList<string> roots, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);
        try
        {
            var descriptors = new List<string>();
            foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) continue;
                AddDescriptors(root, descriptors);
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddDescriptors(directory, descriptors);
                }
            }

            var mods = new List<ModInstallation>();
            var seen = new Dictionary<ModKey, string>();
            for (var index = 0; index < descriptors.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = descriptors[index];
                var directory = Path.GetDirectoryName(descriptor)!;
                var source = IsWorkshopPath(descriptor) ? ModSource.SteamWorkshop : ModSource.Manual;
                var normalized = pathSemantics.NormalizeIdentity(directory);
                if (!normalized.IsSuccess) return Result<IReadOnlyList<ModInstallation>>.Failure(normalized.Error!);
                var key = new ModKey(source, normalized.Value!);
                if (seen.TryGetValue(key, out var previous))
                    return Result<IReadOnlyList<ModInstallation>>.Failure(new Error("catalog.multiple_descriptors", $"Multiple mod descriptors share one physical location: {directory}", ErrorKind.Conflict, new Dictionary<string, string> { ["location"] = key.LocationIdentity, ["descriptors"] = string.Join("|", new[] { previous, descriptor }.Order(StringComparer.OrdinalIgnoreCase)) }));
                seen[key] = descriptor;
                var metadata = LegacyModMetadataParser.Parse(File.ReadAllText(descriptor));
                var packageName = Path.GetFileName(descriptor);
                packageName = packageName.EndsWith(".XComMod-disabled", StringComparison.OrdinalIgnoreCase)
                    ? packageName[..^".XComMod-disabled".Length]
                    : Path.GetFileNameWithoutExtension(packageName);
                mods.Add(new ModInstallation(
                    key,
                    new PackageId(packageName),
                    string.IsNullOrWhiteSpace(metadata.Title) ? packageName : metadata.Title,
                    metadata.PublishedFileId > 0 ? new WorkshopId((ulong)metadata.PublishedFileId) : null,
                    metadata.RequiresExpansion,
                    descriptor.EndsWith("-disabled", StringComparison.OrdinalIgnoreCase) ? DescriptorState.Disabled : DescriptorState.Enabled,
                    new DateTimeOffset(Directory.GetCreationTimeUtc(directory), TimeSpan.Zero),
                    new ModInstallationMetadata(
                        descriptor,
                        metadata.Category,
                        metadata.Description,
                        (metadata.Tags ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        SafeFile(directory, metadata.ContentImage),
                        new[] { "ReadMe.txt", "ReadMe.md", "README.txt", "README.md" }.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists),
                        new DateTimeOffset(Directory.GetCreationTimeUtc(directory), TimeSpan.Zero),
                        new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero))));
                progress?.Report(new OperationProgress("catalog.discover", index + 1, descriptors.Count, key));
            }
            return Result<IReadOnlyList<ModInstallation>>.Success(mods.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ThenBy(mod => mod.Key.LocationIdentity, StringComparer.Ordinal).ToArray());
        }
        catch (OperationCanceledException)
        {
            return Result<IReadOnlyList<ModInstallation>>.Failure(new Error("catalog.cancelled", "Mod discovery was cancelled.", ErrorKind.Cancelled));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<IReadOnlyList<ModInstallation>>.Failure(new Error("catalog.read_failed", exception.Message, ErrorKind.Io));
        }
    }

    private static void AddDescriptors(string directory, List<string> descriptors)
    {
        descriptors.AddRange(Directory.EnumerateFiles(directory, "*.XComMod", SearchOption.TopDirectoryOnly));
        descriptors.AddRange(Directory.EnumerateFiles(directory, "*.XComMod-disabled", SearchOption.TopDirectoryOnly));
    }

    private static bool IsWorkshopPath(string path)
    {
        var components = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length - 1; index++)
            if (components[index].Equals("workshop", StringComparison.OrdinalIgnoreCase) && components[index + 1].Equals("content", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? SafeFile(string root, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var path = Path.GetFullPath(Path.Combine(root, candidate));
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }
}
