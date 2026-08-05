using AAML.Application.Common;
using AAML.Application.Ports;

namespace AAML.Infrastructure.Linux.Paths;

/// <summary>Host-independent lexical POSIX path identity with ordinal case sensitivity.</summary>
public sealed class LinuxPathSemantics : IPathSemantics
{
    public Result<string> NormalizeIdentity(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Failure("path.required", "A path is required.");
        if (path.IndexOf('\0') >= 0) return Failure("path.invalid", "The path contains a null character.");
        if (!path.StartsWith("/", StringComparison.Ordinal)) return Failure("path.not_absolute", "The path must be absolute.");

        var components = new List<string>();
        foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".") continue;
            if (component == "..")
            {
                if (components.Count == 0) return Failure("path.root_escape", "The path escapes above its root.");
                components.RemoveAt(components.Count - 1);
                continue;
            }
            components.Add(component);
        }
        return Result<string>.Success(components.Count == 0 ? "/" : "/" + string.Join('/', components));
    }

    public bool AreEqual(string left, string right)
    {
        var normalizedLeft = NormalizeIdentity(left);
        var normalizedRight = NormalizeIdentity(right);
        return normalizedLeft.IsSuccess && normalizedRight.IsSuccess && string.Equals(normalizedLeft.Value, normalizedRight.Value, StringComparison.Ordinal);
    }

    public Result<bool> IsContainedBy(string candidate, string parent)
    {
        var normalizedCandidate = NormalizeIdentity(candidate);
        if (!normalizedCandidate.IsSuccess) return Result<bool>.Failure(normalizedCandidate.Error!);
        var normalizedParent = NormalizeIdentity(parent);
        if (!normalizedParent.IsSuccess) return Result<bool>.Failure(normalizedParent.Error!);
        if (normalizedCandidate.Value == normalizedParent.Value) return Result<bool>.Success(true);
        var prefix = normalizedParent.Value == "/" ? "/" : normalizedParent.Value + "/";
        return Result<bool>.Success(normalizedCandidate.Value!.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static Result<string> Failure(string code, string message) => Result<string>.Failure(new Error(code, message, ErrorKind.Validation));
}
