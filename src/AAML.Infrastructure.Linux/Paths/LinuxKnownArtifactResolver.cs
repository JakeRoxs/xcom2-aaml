using AAML.Application.Common;
using AAML.Application.Ports;

namespace AAML.Infrastructure.Linux.Paths;

/// <summary>Resolves caller-declared Steam/XCOM path components with an exact-first, unambiguous casing fallback.</summary>
public sealed class LinuxKnownArtifactResolver(IPhysicalPathResolver physicalPathResolver)
{
    private readonly LinuxPathSemantics semantics = new();

    public Result<LinuxKnownArtifactPath> ResolveExistingDirectory(string root, params string[] components) =>
        Resolve(root, components, allowMissingTail: false, LinuxKnownArtifactKind.Directory);

    public Result<LinuxKnownArtifactPath> ResolveExistingFile(string root, params string[] components) =>
        Resolve(root, components, allowMissingTail: false, LinuxKnownArtifactKind.File);

    public Result<LinuxKnownArtifactPath> ResolveDirectoryExistingOrExpected(string root, params string[] components) =>
        Resolve(root, components, allowMissingTail: true, LinuxKnownArtifactKind.Directory);

    private Result<LinuxKnownArtifactPath> Resolve(string root, IReadOnlyList<string> components, bool allowMissingTail, LinuxKnownArtifactKind finalKind)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Count == 0)
            return Failure("path.known_artifact_component_invalid", "At least one known artifact component is required.", ErrorKind.Validation);
        var normalizedRoot = semantics.NormalizeIdentity(root);
        if (!normalizedRoot.IsSuccess) return Result<LinuxKnownArtifactPath>.Failure(normalizedRoot.Error!);
        var rootPhysical = physicalPathResolver.ResolveExisting(normalizedRoot.Value!);
        if (!rootPhysical.IsSuccess) return Result<LinuxKnownArtifactPath>.Failure(rootPhysical.Error!);
        var current = normalizedRoot.Value!;
        var fallbacks = new List<LinuxArtifactCaseFallback>();

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            if (!IsSafeComponent(component))
                return Failure("path.known_artifact_component_invalid", $"Known artifact component is not a safe single path name: {component}", ErrorKind.Validation);
            var exact = Path.Combine(current, component);
            var expectedKind = index == components.Count - 1 ? finalKind : LinuxKnownArtifactKind.Directory;
            if (PathExists(exact))
            {
                if (!MatchesKind(exact, expectedKind))
                    return Failure("path.known_artifact_type_mismatch", $"Known artifact has the wrong filesystem type: {exact}", ErrorKind.InvalidData);
                current = exact;
                continue;
            }

            string[] matches;
            try
            {
                matches = Directory.EnumerateFileSystemEntries(current)
                    .Where(path => Path.GetFileName(path).Equals(component, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Failure("path.known_artifact_enumeration_failed", exception.Message,
                    exception is UnauthorizedAccessException ? ErrorKind.Unauthorized : ErrorKind.Io);
            }

            if (matches.Length > 1)
                return Failure("path.known_artifact_case_ambiguous",
                    $"Known artifact '{component}' is ambiguous beneath '{current}': {string.Join(", ", matches.Select(Path.GetFileName))}", ErrorKind.Conflict);
            if (matches.Length == 1)
            {
                if (!MatchesKind(matches[0], expectedKind))
                    return Failure("path.known_artifact_type_mismatch", $"Known artifact has the wrong filesystem type: {matches[0]}", ErrorKind.InvalidData);
                fallbacks.Add(new LinuxArtifactCaseFallback(exact, matches[0]));
                current = matches[0];
                continue;
            }
            if (!allowMissingTail)
                return Failure("path.known_artifact_missing", $"Known artifact does not exist: {exact}", ErrorKind.NotFound);

            var existingParent = physicalPathResolver.ResolveExisting(current);
            if (!existingParent.IsSuccess) return Result<LinuxKnownArtifactPath>.Failure(existingParent.Error!);
            var existingContainment = semantics.IsContainedBy(existingParent.Value!, rootPhysical.Value!);
            if (!existingContainment.IsSuccess || existingContainment.Value != true)
                return Failure("path.known_artifact_outside_root", "The resolved artifact parent escapes its qualified root.", ErrorKind.Validation);

            for (var remainder = index; remainder < components.Count; remainder++)
            {
                if (!IsSafeComponent(components[remainder]))
                    return Failure("path.known_artifact_component_invalid", $"Known artifact component is not a safe single path name: {components[remainder]}", ErrorKind.Validation);
                current = Path.Combine(current, components[remainder]);
            }
            return Result<LinuxKnownArtifactPath>.Success(new(current, false, fallbacks));
        }

        var resolved = physicalPathResolver.ResolveExisting(current);
        if (!resolved.IsSuccess) return Result<LinuxKnownArtifactPath>.Failure(resolved.Error!);
        var containment = semantics.IsContainedBy(resolved.Value!, rootPhysical.Value!);
        if (!containment.IsSuccess || containment.Value != true)
            return Failure("path.known_artifact_outside_root", "The resolved artifact escapes its qualified root.", ErrorKind.Validation);
        return Result<LinuxKnownArtifactPath>.Success(new(resolved.Value!, true, fallbacks));
    }

    private static bool IsSafeComponent(string component) => !string.IsNullOrWhiteSpace(component) && component is not "." and not ".." &&
        component.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);
    private static bool MatchesKind(string path, LinuxKnownArtifactKind kind) => kind switch
    {
        LinuxKnownArtifactKind.File => File.Exists(path),
        LinuxKnownArtifactKind.Directory => Directory.Exists(path),
        _ => false
    };
    private static Result<LinuxKnownArtifactPath> Failure(string code, string message, ErrorKind kind) =>
        Result<LinuxKnownArtifactPath>.Failure(new Error(code, message, kind));
}

public sealed record LinuxArtifactCaseFallback(string ExpectedPath, string ActualPath);
public sealed record LinuxKnownArtifactPath(string Path, bool Exists, IReadOnlyList<LinuxArtifactCaseFallback> CaseFallbacks);
internal enum LinuxKnownArtifactKind { File, Directory }
