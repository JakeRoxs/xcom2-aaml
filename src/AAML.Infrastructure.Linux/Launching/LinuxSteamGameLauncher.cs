using System.ComponentModel;
using System.Diagnostics;
using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Steam;
using AAML.Domain.Games;
using AAML.Domain.Launching;

namespace AAML.Infrastructure.Linux.Launching;

/// <summary>Publishes one Proton wrapper request and asks the host's native or Flatpak Steam client to launch its app.</summary>
internal interface ISteamAppLauncher
{
    Result<int> Start(SteamAppId appId, bool flatpakSteam);
}

internal sealed class SteamAppLauncher : ISteamAppLauncher
{
    public Result<int> Start(SteamAppId appId, bool flatpakSteam)
    {
        try
        {
            var start = CreateStartInfo(appId, flatpakSteam, Environment.GetEnvironmentVariable("FLATPAK_ID") is not null);
            using var process = Process.Start(start);
            return process is null
                ? Result<int>.Failure(new Error("launch.steam_start_failed", "Steam did not start the launch request.", ErrorKind.ExternalService))
                : Result<int>.Success(process.Id);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return Result<int>.Failure(new Error("launch.steam_start_failed", exception.Message, ErrorKind.ExternalService));
        }
    }

    internal static ProcessStartInfo CreateStartInfo(SteamAppId appId, bool flatpakSteam, bool launcherIsFlatpak)
    {
        var start = new ProcessStartInfo { FileName = launcherIsFlatpak ? "flatpak-spawn" : flatpakSteam ? "flatpak" : "steam", UseShellExecute = false };
        if (launcherIsFlatpak) start.ArgumentList.Add("--host");
        if (flatpakSteam)
        {
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("com.valvesoftware.Steam");
        }
        else if (launcherIsFlatpak) start.ArgumentList.Add("steam");
        start.ArgumentList.Add("-applaunch");
        start.ArgumentList.Add(appId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return start;
    }
}

/// <summary>Linux game launcher that supports both Proton and native Feral runtimes.</summary>
public sealed class LinuxGameLauncher : IGameLauncher
{
    private readonly ISteamLaunchRequestStore requestStore;
    private readonly ISteamAppLauncher steamLauncher;

    public LinuxGameLauncher(ISteamLaunchRequestStore requestStore) : this(requestStore, new SteamAppLauncher()) { }
    internal LinuxGameLauncher(ISteamLaunchRequestStore requestStore, ISteamAppLauncher steamLauncher)
    {
        this.requestStore = requestStore;
        this.steamLauncher = steamLauncher;
    }

    public Task<Result> ValidateAsync(GameLaunchRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(Result.Failure(new Error("launch.cancelled", "Game launch was cancelled.", ErrorKind.Cancelled)));
        var layout = LinuxGameRuntimeLayout.Resolve(request.GameInstallationLocation, request.Variant, request.Runtime);
        return Task.FromResult(layout.IsSuccess ? Result.Success() : Result.Failure(layout.Error!));
    }

    public async Task<Result<GameLaunchReceipt>> LaunchAsync(GameLaunchRequest request, CancellationToken cancellationToken)
    {
        var layout = LinuxGameRuntimeLayout.Resolve(request.GameInstallationLocation, request.Variant, request.Runtime);
        if (!layout.IsSuccess)
            return Result<GameLaunchReceipt>.Failure(layout.Error!);
        var resolved = layout.Value!;

        if (resolved.UsesProtonPrefix)
        {
            // Proton launch: use one-shot wrapper request
            var now = DateTimeOffset.UtcNow;
            var appId = new SteamAppId(AAML.Domain.Games.GameVariantPolicy.GetSteamAppId(request.Variant));
            var steamRequest = new SteamLaunchRequest(
                SteamLaunchRequestPolicy.CurrentProtocolVersion,
                Guid.NewGuid(),
                appId,
                request.Variant,
                resolved.GameInstallPath,
                resolved.TargetExecutablePath,
                request.ActiveMods.OrderBy(mod => mod.Order).Select(mod => mod.PackageId.Value).ToArray(),
                request.ModRootLocations.ToArray(),
                request.Arguments.Select(argument => argument.Value).ToArray(),
                now,
                now + SteamLaunchRequestPolicy.MaximumLifetime);
            var published = await requestStore.PublishAsync(steamRequest, cancellationToken).ConfigureAwait(false);
            if (!published.IsSuccess)
                return Result<GameLaunchReceipt>.Failure(published.Error!);

            var flatpakSteam = request.GameInstallationLocation.Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal);
            var started = steamLauncher.Start(appId, flatpakSteam);
            return started.IsSuccess
                ? Result<GameLaunchReceipt>.Success(new GameLaunchReceipt(now, started.Value, resolved.TargetExecutablePath))
                : Result<GameLaunchReceipt>.Failure(started.Error!);
        }
        else
        {
            // Native launch: spawn the Feral ELF directly
            return await LaunchNativeAsync(resolved, request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Result<GameLaunchReceipt>> LaunchNativeAsync(
        LinuxGameRuntimeLayout layout,
        GameLaunchRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var launcherIsFlatpak = Environment.GetEnvironmentVariable("FLATPAK_ID") is not null;
            var runtime = ResolveSteamRuntime(layout, launcherIsFlatpak);
            if (!runtime.IsSuccess) return Result<GameLaunchReceipt>.Failure(runtime.Error!);
            var appId = AAML.Domain.Games.GameVariantPolicy.GetSteamAppId(request.Variant);
            var launchGameInstallPath = launcherIsFlatpak
                ? MapFlatpakSteamPathToHost(layout.GameInstallPath, runtime.Value.SteamRoot)
                : layout.GameInstallPath;

            // Prefer Feral launcher script when available for proper runtime integration
            var feralScript = ResolveFeralLauncherScript(launchGameInstallPath, request.Variant);
            string target;
            string runner;
            if (feralScript is not null)
            {
                runner = runtime.Value.Runner;
                target = feralScript;
            }
            else
            {
                runner = runtime.Value.Runner;
                target = runtime.Value.Target;
            }
            var startInfo = CreateNativeStartInfo(
                runner,
                target,
                launchGameInstallPath,
                request.Arguments,
                appId,
                launcherIsFlatpak,
                request.Variant == GameVariant.XCom2);

            using var process = Process.Start(startInfo);
            if (process is null)
                return Result<GameLaunchReceipt>.Failure(new Error("launch.process_start_failed", "The native game process did not start.", ErrorKind.ExternalService));

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            if (process.HasExited)
                return Result<GameLaunchReceipt>.Failure(new Error("launch.process_exited", $"The native game process exited during startup with code {process.ExitCode}.", ErrorKind.ExternalService));

            return Result<GameLaunchReceipt>.Success(new GameLaunchReceipt(now, process.Id, layout.TargetExecutablePath));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return Result<GameLaunchReceipt>.Failure(new Error("launch.process_start_failed", exception.Message, ErrorKind.ExternalService));
        }
    }

    internal static ProcessStartInfo CreateNativeStartInfo(string runtimeRunner, string target, string gameInstallPath, IReadOnlyList<LaunchArgument> arguments, uint appId, bool launcherIsFlatpak, bool forceX11 = false)
    {
        var start = new ProcessStartInfo
        {
            FileName = launcherIsFlatpak ? "flatpak-spawn" : runtimeRunner,
            WorkingDirectory = Path.GetDirectoryName(target)!,
            UseShellExecute = false,
        };
        var appIdValue = appId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var libraryPath = string.Join(':', Path.Combine(gameInstallPath, "XCOM2WotC", "lib"), Path.Combine(gameInstallPath, "lib", "x86_64"));
        if (launcherIsFlatpak)
        {
            start.ArgumentList.Add("--host");
            start.ArgumentList.Add($"--env=SteamAppId={appIdValue}");
            start.ArgumentList.Add($"--env=SteamGameId={appIdValue}");
            start.ArgumentList.Add($"--env=LD_LIBRARY_PATH={libraryPath}");
            if (forceX11) start.ArgumentList.Add("--env=SDL_VIDEODRIVER=x11");
            start.ArgumentList.Add($"--directory={start.WorkingDirectory}");
            start.ArgumentList.Add(runtimeRunner);
        }
        else
        {
            start.Environment["SteamAppId"] = appIdValue;
            start.Environment["SteamGameId"] = appIdValue;
            var existingLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
            start.Environment["LD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(existingLibraryPath) ? libraryPath : $"{libraryPath}:{existingLibraryPath}";
            if (forceX11) start.Environment["SDL_VIDEODRIVER"] = "x11";
        }
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(target);
        foreach (var argument in arguments) start.ArgumentList.Add(argument.Value);
        return start;
    }

    internal static string? ResolveFeralLauncherScript(string gameInstallPath, GameVariant variant)
    {
        var launcherPaths = variant == GameVariant.XCom2
            ? new[] { "XCOM2.sh" }
            : new[] { Path.Combine("XCOM2WotC", "XCOM2WotC.sh"), "XCOM2WotC.sh" };
        foreach (var relativePath in launcherPaths)
        {
            var path = Path.Combine(gameInstallPath, relativePath);
            if (File.Exists(path))
            {
                var info = new System.IO.FileInfo(path);
                var attributes = info.Attributes;
                if ((attributes & System.IO.FileAttributes.ReadOnly) == 0)
                    return path;
            }
        }
        return null;
    }

    private static Result<(string Runner, string Target, string SteamRoot)> ResolveSteamRuntime(LinuxGameRuntimeLayout layout, bool launcherIsFlatpak)
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        var selectedSteamRoot = Directory.GetParent(layout.SteamAppsPath)?.FullName;
        var roots = new List<string>();
        if (!launcherIsFlatpak && selectedSteamRoot is not null) roots.Add(selectedSteamRoot);
        if (!string.IsNullOrWhiteSpace(home))
        {
            roots.Add(Path.Combine(home, ".steam", "root"));
            roots.Add(Path.Combine(home, ".local", "share", "Steam"));
            roots.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
        }

        // Try newer SteamLinuxRuntime first, then fall back to legacy ubuntu12_32 runtime
        // Steam's native launch uses Pressure Vessel: scout-on-soldier-entry-point-v2
        var runtimePaths = new string[] {
            "steamapps/common/SteamLinuxRuntime/scout-on-soldier-entry-point-v2",
            "steamapps/common/SteamLinuxRuntime/steam-runtime/run.sh",
            "ubuntu12_32/steam-runtime/run.sh"
        };
        foreach (var runtimeRelativePath in runtimePaths)
        {
            foreach (var root in roots.Distinct(StringComparer.Ordinal))
            {
                var runner = Path.Combine(root, runtimeRelativePath);
                if (!File.Exists(runner)) continue;
                var target = launcherIsFlatpak ? MapFlatpakSteamPathToHost(layout.TargetExecutablePath, root) : layout.TargetExecutablePath;
                return Result<(string Runner, string Target, string SteamRoot)>.Success((runner, target, root));
            }
        }

        return Result<(string Runner, string Target, string SteamRoot)>.Failure(new Error("launch.steam_runtime_missing", "Steam's Linux runtime was not found. Start or repair the native Steam client before launching the native game.", ErrorKind.NotFound));
    }

    internal static string MapFlatpakSteamPathToHost(string path, string hostSteamRoot)
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome)) return path;
        var sandboxSteamRoot = Path.Combine(dataHome, "Steam");
        var relative = Path.GetRelativePath(sandboxSteamRoot, path);
        return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? path
            : Path.GetFullPath(Path.Combine(hostSteamRoot, relative));
    }
}
