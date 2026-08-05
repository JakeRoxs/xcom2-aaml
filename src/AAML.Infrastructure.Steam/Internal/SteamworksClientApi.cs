using Steamworks;

namespace AAML.Infrastructure.Steam.Internal;

internal sealed class SteamworksClientApi : ISteamClientApi
{
    public SteamInitialization Initialize()
    {
        try
        {
            var result = SteamAPI.InitEx(out var diagnostic);
            return result switch
            {
                ESteamAPIInitResult.k_ESteamAPIInitResult_OK => new(true, string.Empty, diagnostic),
                ESteamAPIInitResult.k_ESteamAPIInitResult_NoSteamClient => new(false, "steam.not_running", diagnostic),
                ESteamAPIInitResult.k_ESteamAPIInitResult_VersionMismatch => new(false, "steam.version_mismatch", diagnostic),
                _ => new(false, "steam.initialization_failed", diagnostic)
            };
        }
        catch (DllNotFoundException exception)
        {
            return new(false, "steam.native_asset_missing", exception.Message);
        }
        catch (BadImageFormatException exception)
        {
            return new(false, "steam.native_asset_invalid", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return new(false, "steam.unsupported_platform", exception.Message);
        }
    }

    public void RunCallbacks() => SteamAPI.RunCallbacks();

    public void Shutdown() => SteamAPI.Shutdown();
}
