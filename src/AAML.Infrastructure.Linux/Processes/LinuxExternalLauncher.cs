using System.ComponentModel;
using System.Diagnostics;
using AAML.Application.Common;
using AAML.Application.Ports;

namespace AAML.Infrastructure.Linux.Processes;

internal interface ILinuxProcessStarter
{
    int? Start(ProcessStartInfo startInfo);
}

internal sealed class SystemLinuxProcessStarter : ILinuxProcessStarter
{
    public int? Start(ProcessStartInfo startInfo) => Process.Start(startInfo)?.Id;
}

/// <summary>Opens trusted URIs and existing paths directly without invoking a command shell.</summary>
public sealed class LinuxExternalLauncher : IExternalLauncher
{
    private readonly ILinuxProcessStarter starter;
    private readonly Func<string, string?> environment;

    public LinuxExternalLauncher() : this(new SystemLinuxProcessStarter(), Environment.GetEnvironmentVariable) { }
    internal LinuxExternalLauncher(ILinuxProcessStarter starter, Func<string, string?> environment)
    {
        this.starter = starter;
        this.environment = environment;
    }

    public Task<Result> OpenUriAsync(Uri uri, CancellationToken cancellationToken) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" or "steam"
        ? OpenWithXdgAsync(uri.AbsoluteUri, cancellationToken)
        : Task.FromResult(Invalid("Only absolute HTTP, HTTPS, and Steam links can be opened."));

    public Task<Result> OpenFileAsync(string path, CancellationToken cancellationToken) => File.Exists(path)
        ? OpenWithXdgAsync(Path.GetFullPath(path), cancellationToken)
        : Task.FromResult(Invalid("The file does not exist."));

    public Task<Result> OpenDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path)) return Task.FromResult(Invalid("The directory does not exist."));
        var target = Path.GetFullPath(path);
        if (IsKdeSession())
        {
            var dolphin = Start("dolphin", target, cancellationToken);
            if (dolphin.IsSuccess) return Task.FromResult(dolphin);
        }
        return OpenWithXdgAsync(target, cancellationToken);
    }

    private Task<Result> OpenWithXdgAsync(string target, CancellationToken cancellationToken) => Task.FromResult(Start("xdg-open", target, cancellationToken));

    private Result Start(string executable, string target, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Result.Failure(new Error("shell.cancelled", "Opening the target was cancelled.", ErrorKind.Cancelled));
        try
        {
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
            startInfo.ArgumentList.Add(target);
            starter.Start(startInfo);
            return Result.Success();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return Result.Failure(new Error("shell.open_failed", exception.Message, ErrorKind.Io));
        }
    }

    private bool IsKdeSession() => new[] { environment("XDG_CURRENT_DESKTOP"), environment("DESKTOP_SESSION"), environment("KDE_FULL_SESSION") }
        .Any(value => !string.IsNullOrWhiteSpace(value) && (value.Contains("KDE", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("plasma", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase)));

    private static Result Invalid(string message) => Result.Failure(new Error("shell.target_invalid", message, ErrorKind.Validation));
}
