using System.ComponentModel;
using System.Diagnostics;
using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Steam;
using AAML.Domain.Launching;

namespace AAML.Infrastructure.Linux.Launching;

/// <summary>Publishes one Proton wrapper request and asks the native Steam client to launch its app.</summary>
internal interface ISteamAppLauncher
{
    Result<int> Start(SteamAppId appId);
}

internal sealed class SteamAppLauncher : ISteamAppLauncher
{
    public Result<int> Start(SteamAppId appId)
    {
        try
        {
            var start = new ProcessStartInfo { FileName = "steam", UseShellExecute = false };
            start.ArgumentList.Add("-applaunch");
            start.ArgumentList.Add(appId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
}

public sealed class LinuxSteamGameLauncher : IGameLauncher
{
    private readonly ISteamLaunchRequestStore requestStore;
    private readonly ISteamAppLauncher steamLauncher;

    public LinuxSteamGameLauncher(ISteamLaunchRequestStore requestStore) : this(requestStore, new SteamAppLauncher()) { }
    internal LinuxSteamGameLauncher(ISteamLaunchRequestStore requestStore, ISteamAppLauncher steamLauncher)
    {
        this.requestStore = requestStore;
        this.steamLauncher = steamLauncher;
    }

    public Task<Result> ValidateAsync(GameLaunchRequest request, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(Result.Failure(new Error("launch.cancelled", "Game launch was cancelled.", ErrorKind.Cancelled)));
        var layout = LinuxSteamGameLayout.Resolve(request.GameInstallationLocation, request.Variant);
        return Task.FromResult(layout.IsSuccess ? Result.Success() : Result.Failure(layout.Error!));
    }

    public async Task<Result<GameLaunchReceipt>> LaunchAsync(GameLaunchRequest request, CancellationToken cancellationToken)
    {
        var layout = LinuxSteamGameLayout.Resolve(request.GameInstallationLocation, request.Variant);
        if (!layout.IsSuccess) return Result<GameLaunchReceipt>.Failure(layout.Error!);
        var now = DateTimeOffset.UtcNow;
        var appId = new SteamAppId(AAML.Domain.Games.GameVariantPolicy.GetSteamAppId(request.Variant));
        var steamRequest = new SteamLaunchRequest(SteamLaunchRequestPolicy.CurrentProtocolVersion, Guid.NewGuid(), appId, request.Variant,
            layout.Value!.GameInstallPath, layout.Value.TargetExecutablePath,
            request.ActiveMods.OrderBy(mod => mod.Order).Select(mod => mod.PackageId.Value).ToArray(), request.ModRootLocations.ToArray(),
            request.Arguments.Select(argument => argument.Value).ToArray(), now, now + SteamLaunchRequestPolicy.MaximumLifetime);
        var published = await requestStore.PublishAsync(steamRequest, cancellationToken).ConfigureAwait(false);
        if (!published.IsSuccess) return Result<GameLaunchReceipt>.Failure(published.Error!);
        var started = steamLauncher.Start(appId);
        return started.IsSuccess
            ? Result<GameLaunchReceipt>.Success(new GameLaunchReceipt(now, started.Value, layout.Value.TargetExecutablePath))
            : Result<GameLaunchReceipt>.Failure(started.Error!);
    }
}
