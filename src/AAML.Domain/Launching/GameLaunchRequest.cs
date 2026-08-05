using AAML.Domain.Games;
using AAML.Domain.Mods;

namespace AAML.Domain.Launching;

/// <summary>One structured game argument.</summary>
public readonly record struct LaunchArgument
{
    public LaunchArgument(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }
}

/// <summary>One active mod in deterministic game load order.</summary>
public sealed record GameLaunchMod(ModKey Mod, PackageId PackageId, int Order, bool RequiresWarOfTheChosen);

/// <summary>Platform-neutral intent to configure and launch one installed game mode.</summary>
public sealed record GameLaunchRequest(
    GameVariant Variant,
    string GameInstallationLocation,
    IReadOnlyList<string> ModRootLocations,
    IReadOnlyList<GameLaunchMod> ActiveMods,
    IReadOnlyList<LaunchArgument> Arguments,
    bool ApplyConfiguration = true);

/// <summary>Creates launch intent without reproducing legacy whole-command-line lowercasing.</summary>
public static class GameLaunchPolicy
{
    /// <summary>Applies game-mode argument restrictions while preserving argument casing.</summary>
    public static GameLaunchRequest Normalize(GameLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Variant != GameVariant.XCom2WarOfTheChosenChallengeMode)
        {
            return request;
        }

        var arguments = request.Arguments
            .Where(argument => !argument.Value.Equals("-allowConsole", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return request with { Arguments = arguments, ApplyConfiguration = false };
    }
}
