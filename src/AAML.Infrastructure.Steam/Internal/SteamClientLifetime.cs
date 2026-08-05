using AAML.Application.Common;

namespace AAML.Infrastructure.Steam.Internal;

internal sealed class SteamClientLifetime(ISteamClientApi api, SteamOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private Task? pumpTask;
    private bool running;
    private bool disposed;
    private string? previousSteamAppId;
    private string? previousSteamGameId;
    private bool environmentConfigured;

    public CancellationToken StoppingToken => stopping.Token;

    public async Task<Result> StartAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return Result.Failure(new Error("steam.disposed", "The Steam lifetime is disposed.", ErrorKind.Unavailable));
            }

            if (running)
            {
                return Result.Success();
            }

            ConfigureEnvironment();
            var initialization = api.Initialize();
            if (!initialization.IsSuccess)
            {
                RestoreEnvironment();
                return Result.Failure(new Error(initialization.Code, initialization.Diagnostic, ErrorKind.Unavailable));
            }

            running = true;
            pumpTask = PumpAsync(stopping.Token);
            return Result.Success();
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            stopping.Cancel();
        }
        finally
        {
            gate.Release();
        }

        if (pumpTask is not null)
        {
            try
            {
                await pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (running)
        {
            api.Shutdown();
            running = false;
        }
        RestoreEnvironment();

        stopping.Dispose();
        gate.Dispose();
    }

    private void ConfigureEnvironment()
    {
        if (environmentConfigured || options.AppId == 0) return;
        previousSteamAppId = Environment.GetEnvironmentVariable("SteamAppId");
        previousSteamGameId = Environment.GetEnvironmentVariable("SteamGameId");
        var value = options.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Environment.SetEnvironmentVariable("SteamAppId", value);
        Environment.SetEnvironmentVariable("SteamGameId", value);
        environmentConfigured = true;
    }

    private void RestoreEnvironment()
    {
        if (!environmentConfigured) return;
        Environment.SetEnvironmentVariable("SteamAppId", previousSteamAppId);
        Environment.SetEnvironmentVariable("SteamGameId", previousSteamGameId);
        environmentConfigured = false;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.CallbackInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            api.RunCallbacks();
        }
    }
}
