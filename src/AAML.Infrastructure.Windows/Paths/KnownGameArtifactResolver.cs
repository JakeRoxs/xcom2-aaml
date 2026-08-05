using AAML.Application.Common;
using AAML.Application.Ports;

namespace AAML.Infrastructure.Windows.Paths;

/// <summary>Resolves only caller-specified known artifact components with narrow case-insensitive fallback.</summary>
public sealed class KnownGameArtifactResolver : IKnownGameArtifactResolver
{
    public Task<Result<ResolvedGameArtifact>> ResolveAsync(KnownGameArtifactRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(request.RootDirectory))
        {
            return Task.FromResult(Failure("artifact.root_not_found", "The artifact root does not exist.", ErrorKind.NotFound));
        }

        try
        {
            var current = request.RootDirectory;
            var actual = new List<string>();
            for (var index = 0; index < request.RelativeComponents.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = request.RelativeComponents[index];
                if (string.IsNullOrWhiteSpace(requested) || requested.IndexOfAny(['/', '\\']) >= 0)
                {
                    return Task.FromResult(Failure("artifact.invalid_component", "Artifact components must be individual names.", ErrorKind.Validation));
                }

                var candidates = Directory.EnumerateFileSystemEntries(current)
                    .Select(Path.GetFileName)
                    .Where(name => name is not null)
                    .Cast<string>()
                    .ToArray();
                var exact = candidates.SingleOrDefault(name => string.Equals(name, requested, StringComparison.Ordinal));
                var insensitive = candidates.Where(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
                if (exact is not null)
                {
                    current = Path.Combine(current, exact);
                    actual.Add(exact);
                }
                else if (insensitive.Length == 1)
                {
                    current = Path.Combine(current, insensitive[0]);
                    actual.Add(insensitive[0]);
                }
                else if (insensitive.Length > 1)
                {
                    return Task.FromResult(Result<ResolvedGameArtifact>.Failure(new Error(
                        "artifact.ambiguous",
                        "Multiple artifact components differ only by casing.",
                        ErrorKind.Conflict,
                        new Dictionary<string, string> { ["componentIndex"] = index.ToString(), ["candidates"] = string.Join('|', insensitive) })));
                }
                else
                {
                    return Task.FromResult(Failure("artifact.not_found", "A required artifact component was not found.", ErrorKind.NotFound));
                }
            }

            var correctType = request.Kind == ArtifactKind.File ? File.Exists(current) : Directory.Exists(current);
            return Task.FromResult(correctType
                ? Result<ResolvedGameArtifact>.Success(new ResolvedGameArtifact(current, actual))
                : Failure("artifact.type_mismatch", "The resolved artifact has the wrong type.", ErrorKind.InvalidData));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Task.FromResult(Failure("artifact.access_denied", exception.Message, ErrorKind.Unauthorized));
        }
        catch (IOException exception)
        {
            return Task.FromResult(Failure("artifact.io", exception.Message, ErrorKind.Io));
        }
    }

    private static Result<ResolvedGameArtifact> Failure(string code, string message, ErrorKind kind) =>
        Result<ResolvedGameArtifact>.Failure(new Error(code, message, kind));
}
