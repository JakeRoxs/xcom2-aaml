using System.ComponentModel;
using System.Diagnostics;
using AAML.Application.Common;
using AAML.Application.Ports;

namespace AAML.Infrastructure.Windows.Processes;

internal interface IProcessStarter
{
    int? Start(ProcessStartInfo startInfo);
}

internal sealed class SystemProcessStarter : IProcessStarter
{
    public int? Start(ProcessStartInfo startInfo) => Process.Start(startInfo)?.Id;
}

/// <summary>Starts executables directly without invoking a command shell.</summary>
public sealed class WindowsProcessRunner : IProcessRunner
{
    private readonly IProcessStarter starter;
    public WindowsProcessRunner() : this(new SystemProcessStarter()) { }
    internal WindowsProcessRunner(IProcessStarter starter) => this.starter = starter;

    public Task<Result<ProcessStartResult>> StartAsync(ProcessLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(Result<ProcessStartResult>.Failure(new Error("process.cancelled", "Process start was cancelled.", ErrorKind.Cancelled)));
        try
        {
            var info = new ProcessStartInfo { FileName = request.ExecutablePath, UseShellExecute = false };
            if (!string.IsNullOrWhiteSpace(request.WorkingDirectory)) info.WorkingDirectory = request.WorkingDirectory;
            foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
            var processId = starter.Start(info);
            return Task.FromResult(processId is null
                ? Result<ProcessStartResult>.Failure(new Error("process.start_failed", "The process did not start.", ErrorKind.ExternalService))
                : Result<ProcessStartResult>.Success(new ProcessStartResult(processId)));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return Task.FromResult(Result<ProcessStartResult>.Failure(new Error("process.start_failed", exception.Message, ErrorKind.ExternalService)));
        }
    }
}

/// <summary>Uses Windows shell association only for validated external targets.</summary>
public sealed class WindowsExternalLauncher : IExternalLauncher
{
    private readonly IProcessStarter starter;
    public WindowsExternalLauncher() : this(new SystemProcessStarter()) { }
    internal WindowsExternalLauncher(IProcessStarter starter) => this.starter = starter;

    public Task<Result> OpenUriAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "steam"))
            return Task.FromResult(Result.Failure(new Error("shell.uri_scheme", "The URI scheme is not allowed.", ErrorKind.Validation)));
        return OpenAsync(uri.AbsoluteUri, cancellationToken);
    }

    public Task<Result> OpenFileAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? OpenAsync(path, cancellationToken) : Task.FromResult(Result.Failure(new Error("shell.file_not_found", "The file does not exist.", ErrorKind.NotFound)));

    public Task<Result> OpenDirectoryAsync(string path, CancellationToken cancellationToken) =>
        Directory.Exists(path) ? OpenAsync(path, cancellationToken) : Task.FromResult(Result.Failure(new Error("shell.directory_not_found", "The directory does not exist.", ErrorKind.NotFound)));

    private Task<Result> OpenAsync(string target, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(Result.Failure(new Error("shell.cancelled", "External launch was cancelled.", ErrorKind.Cancelled)));
        try
        {
            starter.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            return Task.FromResult(Result.Success());
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return Task.FromResult(Result.Failure(new Error("shell.open_failed", exception.Message, ErrorKind.ExternalService)));
        }
    }
}
