using AAML.Application.Common;

namespace AAML.Infrastructure.Linux.Paths;

/// <summary>Normalizes already-discovered Steam library roots without performing discovery.</summary>
public static class LinuxSteamLibraryPaths
{
    public static Result<IReadOnlyList<string>> Normalize(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var semantics = new LinuxPathSemantics();
        var normalized = new List<string>();
        foreach (var path in paths)
        {
            var result = semantics.NormalizeIdentity(path);
            if (!result.IsSuccess) return Result<IReadOnlyList<string>>.Failure(result.Error!);
            if (!normalized.Contains(result.Value!, StringComparer.Ordinal)) normalized.Add(result.Value!);
        }
        return Result<IReadOnlyList<string>>.Success(normalized);
    }
}
