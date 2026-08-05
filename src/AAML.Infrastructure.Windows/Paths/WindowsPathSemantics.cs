using AAML.Application.Common;
using AAML.Application.Ports;

namespace AAML.Infrastructure.Windows.Paths;

/// <summary>Lexical Windows path identity independent of the host running the code.</summary>
public sealed class WindowsPathSemantics : IPathSemantics
{
    public Result<string> NormalizeIdentity(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure("path.required", "A path is required.");
        }

        if (path.IndexOf('\0') >= 0)
        {
            return Failure("path.invalid", "The path contains a null character.");
        }

        var normalized = path.Replace('/', '\\');
        if (normalized.StartsWith("\\\\?\\", StringComparison.Ordinal) || normalized.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            return Failure("path.unsupported_format", "Device and extended paths are not supported.");
        }

        string root;
        string remainder;
        if (normalized.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var components = normalized[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length < 2)
            {
                return Failure("path.not_absolute", "A UNC path requires a server and share.");
            }

            root = $"\\\\{components[0]}\\{components[1]}";
            remainder = string.Join('\\', components.Skip(2));
        }
        else if (normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '\\')
        {
            root = char.ToUpperInvariant(normalized[0]) + ":";
            remainder = normalized[3..];
        }
        else
        {
            return Failure("path.not_absolute", "The path must be drive-rooted or UNC-rooted.");
        }

        var stack = new List<string>();
        foreach (var component in remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (stack.Count == 0)
                {
                    return Failure("path.root_escape", "The path escapes above its root.");
                }

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(component);
        }

        var separator = root.StartsWith("\\\\", StringComparison.Ordinal) ? "\\" : ":";
        var canonicalRoot = separator == ":" ? root + "\\" : root + "\\";
        return Result<string>.Success(stack.Count == 0 ? canonicalRoot : canonicalRoot + string.Join('\\', stack));
    }

    public bool AreEqual(string left, string right)
    {
        var normalizedLeft = NormalizeIdentity(left);
        var normalizedRight = NormalizeIdentity(right);
        return normalizedLeft.IsSuccess && normalizedRight.IsSuccess &&
               string.Equals(normalizedLeft.Value, normalizedRight.Value, StringComparison.OrdinalIgnoreCase);
    }

    public Result<bool> IsContainedBy(string candidate, string parent)
    {
        var normalizedCandidate = NormalizeIdentity(candidate);
        if (!normalizedCandidate.IsSuccess)
        {
            return Result<bool>.Failure(normalizedCandidate.Error!);
        }

        var normalizedParent = NormalizeIdentity(parent);
        if (!normalizedParent.IsSuccess)
        {
            return Result<bool>.Failure(normalizedParent.Error!);
        }

        if (string.Equals(normalizedCandidate.Value, normalizedParent.Value, StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Success(true);
        }

        var prefix = normalizedParent.Value!.EndsWith('\\') ? normalizedParent.Value : normalizedParent.Value + "\\";
        return Result<bool>.Success(normalizedCandidate.Value!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static Result<string> Failure(string code, string message) => Result<string>.Failure(new Error(code, message, ErrorKind.Validation));
}
