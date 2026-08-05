using System.Runtime.InteropServices;
using AAML.Application.Common;
using AAML.Application.Ports;

namespace AAML.Infrastructure.Linux.Paths;

/// <summary>Linux realpath adapter for existing physical filesystem identity.</summary>
public sealed partial class LinuxPhysicalPathResolver : IPhysicalPathResolver
{
    public Result<string> ResolveExisting(string path)
    {
        if (!OperatingSystem.IsLinux())
            return Failure("path.platform_unsupported", "Physical Linux path resolution requires Linux.", ErrorKind.Unavailable);
        var lexical = new LinuxPathSemantics().NormalizeIdentity(path);
        if (!lexical.IsSuccess) return lexical;
        var pointer = RealPath(lexical.Value!, IntPtr.Zero);
        if (pointer != IntPtr.Zero)
        {
            try { return Result<string>.Success(Marshal.PtrToStringUTF8(pointer)!); }
            finally { Free(pointer); }
        }
        return Marshal.GetLastPInvokeError() switch
        {
            2 => Failure("path.not_found", "The path does not exist.", ErrorKind.NotFound),
            13 => Failure("path.access_denied", "Access to the path was denied.", ErrorKind.Unauthorized),
            20 => Failure("path.not_directory", "A path component is not a directory.", ErrorKind.InvalidData),
            40 => Failure("path.symlink_loop", "The path contains a symbolic-link loop.", ErrorKind.InvalidData),
            var error => Failure("path.resolve_failed", $"realpath failed with errno {error}.", ErrorKind.Io)
        };
    }

    private static Result<string> Failure(string code, string message, ErrorKind kind) => Result<string>.Failure(new Error(code, message, kind));

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial IntPtr RealPath(string path, IntPtr resolvedPath);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(IntPtr pointer);
}
