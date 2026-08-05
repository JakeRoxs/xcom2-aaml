using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;
using AAML.Domain.Launching;

namespace AAML.Infrastructure.Windows.Launching;

/// <summary>Resolves known Windows game artifacts and starts them without a command shell.</summary>
public sealed class WindowsGameLauncher(IProcessRunner processRunner) : IGameLauncher
{
    public Task<Result> ValidateAsync(GameLaunchRequest request, CancellationToken cancellationToken)
    {
        var resolved = Resolve(request, cancellationToken);
        return Task.FromResult(resolved.IsSuccess ? Result.Success() : Result.Failure(resolved.Error!));
    }

    public async Task<Result<GameLaunchReceipt>> LaunchAsync(GameLaunchRequest request, CancellationToken cancellationToken)
    {
        var resolved = Resolve(request, cancellationToken);
        if (!resolved.IsSuccess) return Result<GameLaunchReceipt>.Failure(resolved.Error!);
        var started = await processRunner.StartAsync(new ProcessLaunchRequest(resolved.Value!.Executable, request.Arguments.Select(argument => argument.Value), resolved.Value.WorkingDirectory), cancellationToken).ConfigureAwait(false);
        return started.IsSuccess
            ? Result<GameLaunchReceipt>.Success(new GameLaunchReceipt(DateTimeOffset.UtcNow, started.Value!.ProcessId, resolved.Value.Executable))
            : Result<GameLaunchReceipt>.Failure(started.Error!);
    }

    private static Result<ResolvedWindowsGame> Resolve(GameLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested) return Result<ResolvedWindowsGame>.Failure(new Error("launch.cancelled", "Game launch was cancelled.", ErrorKind.Cancelled));
        try
        {
            var root = Path.GetFullPath(request.GameInstallationLocation);
            if (!Directory.Exists(root)) return Result<ResolvedWindowsGame>.Failure(new Error("launch.installation_missing", "The selected game installation does not exist.", ErrorKind.NotFound));
            var relativeRoot = request.Variant is GameVariant.XCom2 or GameVariant.ChimeraSquad ? string.Empty : "XCom2-WarOfTheChosen";
            var workingDirectory = Path.GetFullPath(Path.Combine(root, relativeRoot));
            var executableName = request.Variant == GameVariant.ChimeraSquad ? "xcom.exe" : "XCom2.exe";
            var executable = Path.GetFullPath(Path.Combine(workingDirectory, "Binaries", "Win64", executableName));
            if (!IsContained(executable, root)) return Result<ResolvedWindowsGame>.Failure(new Error("launch.target_outside_installation", "The game executable resolved outside the selected installation.", ErrorKind.Validation));
            return File.Exists(executable)
                ? Result<ResolvedWindowsGame>.Success(new ResolvedWindowsGame(executable, workingDirectory))
                : Result<ResolvedWindowsGame>.Failure(new Error("launch.executable_missing", $"The selected game executable does not exist: {executable}", ErrorKind.NotFound));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Result<ResolvedWindowsGame>.Failure(new Error("launch.path_invalid", exception.Message, ErrorKind.Validation));
        }
    }

    private static bool IsContained(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private sealed record ResolvedWindowsGame(string Executable, string WorkingDirectory);
}
