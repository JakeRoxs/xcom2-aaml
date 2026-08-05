using System.ComponentModel;
using System.Diagnostics;
using AAML.Application.Common;
using AAML.Application.Ports;

namespace AAML.Infrastructure.Linux.Processes;

public sealed class LinuxExternalLauncher : IExternalLauncher
{
    public Task<Result> OpenUriAsync(Uri uri, CancellationToken cancellationToken) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" or "steam" ? OpenAsync(uri.AbsoluteUri, cancellationToken) : Task.FromResult(Invalid("Only absolute HTTP, HTTPS, and Steam links can be opened."));
    public Task<Result> OpenFileAsync(string path, CancellationToken cancellationToken) => File.Exists(path) ? OpenAsync(Path.GetFullPath(path), cancellationToken) : Task.FromResult(Invalid("The file does not exist."));
    public Task<Result> OpenDirectoryAsync(string path, CancellationToken cancellationToken) => Directory.Exists(path) ? OpenAsync(Path.GetFullPath(path), cancellationToken) : Task.FromResult(Invalid("The directory does not exist."));
    private static Task<Result> OpenAsync(string target, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(Result.Failure(new Error("shell.cancelled", "Opening the target was cancelled.", ErrorKind.Cancelled)));
        try { Process.Start(new ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { target } }); return Task.FromResult(Result.Success()); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException) { return Task.FromResult(Result.Failure(new Error("shell.open_failed", exception.Message, ErrorKind.Io))); }
    }
    private static Result Invalid(string message) => Result.Failure(new Error("shell.target_invalid", message, ErrorKind.Validation));
}
