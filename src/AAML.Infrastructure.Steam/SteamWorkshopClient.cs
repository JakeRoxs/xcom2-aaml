using AAML.Application.Ports;
using AAML.Infrastructure.Steam.Internal;

namespace AAML.Infrastructure.Steam;

/// <summary>Owns the production Workshop service and Steam client lifetime.</summary>
public sealed class SteamWorkshopClient : IAsyncDisposable
{
    private readonly SteamworksCallbacks callbacks;
    private readonly SteamClientLifetime lifetime;

    private SteamWorkshopClient(SteamOptions options)
    {
        callbacks = new SteamworksCallbacks();
        lifetime = new SteamClientLifetime(new SteamworksClientApi(), options);
        Workshop = new SteamWorkshopService(lifetime, new SteamworksUgcApi(), callbacks, options);
    }

    /// <summary>Gets the application-owned Workshop service.</summary>
    public IWorkshopService Workshop { get; }

    /// <summary>Creates one process-owned production Steam client.</summary>
    public static SteamWorkshopClient Create(SteamOptions? options = null) => new(options ?? SteamOptions.Default);

    public async ValueTask DisposeAsync()
    {
        callbacks.Dispose();
        await lifetime.DisposeAsync().ConfigureAwait(false);
    }
}
