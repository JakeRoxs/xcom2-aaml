using AAML.Application.Common;
using AAML.Domain.Games;

namespace AAML.Application.Steam;

public sealed record SteamLaunchRequest(
    int ProtocolVersion,
    Guid RequestId,
    SteamAppId AppId,
    GameVariant Variant,
    string GameInstallPath,
    string TargetExecutablePath,
    IReadOnlyList<string> ActivePackageIds,
    IReadOnlyList<string> ModRootLocations,
    IReadOnlyList<string> AdditionalArguments,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record SteamLaunchTicket(Guid RequestId, SteamAppId AppId, DateTimeOffset ExpiresAtUtc);
public sealed record ClaimedSteamLaunchRequest(SteamLaunchRequest Request, string ClaimedPath);
public sealed record ProtonCommandPlan(IReadOnlyList<string> Tokens, IReadOnlyDictionary<string, string> Environment, bool IsPassThrough);

public interface ISteamLaunchRequestStore
{
    Task<Result<SteamLaunchTicket>> PublishAsync(SteamLaunchRequest request, CancellationToken cancellationToken);
    Task<Result<ClaimedSteamLaunchRequest?>> TryClaimAsync(SteamAppId invokedAppId, DateTimeOffset now, CancellationToken cancellationToken);
}

public static class SteamLaunchRequestPolicy
{
    public const int CurrentProtocolVersion = 2;
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(5);

    public static Result Validate(SteamLaunchRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != CurrentProtocolVersion) return Failure("steam.launch.protocol_unsupported", "The launch protocol version is unsupported.", ErrorKind.InvalidData);
        if (request.RequestId == Guid.Empty) return Failure("steam.launch.request_malformed", "The request ID is empty.", ErrorKind.InvalidData);
        if (GameVariantPolicy.GetSteamAppId(request.Variant) != request.AppId.Value) return Failure("steam.launch.wrong_app_id", "The variant and Steam app ID disagree.", ErrorKind.Conflict);
        if (request.CreatedAtUtc > now + FutureTolerance) return Failure("steam.launch.request_not_yet_valid", "The request creation time is in the future.", ErrorKind.InvalidData);
        if (request.ExpiresAtUtc <= now) return Failure("steam.launch.request_expired", "The launch request expired.", ErrorKind.Timeout);
        if (request.ExpiresAtUtc <= request.CreatedAtUtc || request.ExpiresAtUtc - request.CreatedAtUtc > MaximumLifetime) return Failure("steam.launch.request_malformed", "The request lifetime is invalid.", ErrorKind.InvalidData);
        if (string.IsNullOrWhiteSpace(request.GameInstallPath) || string.IsNullOrWhiteSpace(request.TargetExecutablePath) || !request.GameInstallPath.StartsWith("/", StringComparison.Ordinal) || !request.TargetExecutablePath.StartsWith("/", StringComparison.Ordinal)) return Failure("steam.launch.target_mismatch", "Install and executable paths must be absolute POSIX paths.", ErrorKind.Validation);
        var install = request.GameInstallPath.Replace('\\', '/').TrimEnd('/');
        var target = request.TargetExecutablePath.Replace('\\', '/');
        if (target.Split('/').Contains("..", StringComparer.Ordinal) || !target.StartsWith(install + "/", StringComparison.Ordinal)) return Failure("steam.launch.target_outside_install", "The executable is outside the selected game installation.", ErrorKind.Unauthorized);
        if (!MatchesVariantTarget(request.Variant, request.TargetExecutablePath)) return Failure("steam.launch.target_mismatch", "The target executable does not match the selected game variant.", ErrorKind.Validation);
        if (request.ActivePackageIds is null || request.ActivePackageIds.Count > 10_000 || request.ActivePackageIds.Any(package => string.IsNullOrWhiteSpace(package) || package.Length > 1024 || package.Contains('\0'))) return Failure("steam.launch.mod_invalid", "An active package ID is invalid.", ErrorKind.Validation);
        if (request.ModRootLocations is null || request.ModRootLocations.Count > 256 || request.ModRootLocations.Any(root => string.IsNullOrWhiteSpace(root) || !root.StartsWith("/", StringComparison.Ordinal) || root.Contains('\0'))) return Failure("steam.launch.mod_root_invalid", "A mod root is invalid.", ErrorKind.Validation);
        if (request.AdditionalArguments is null || request.AdditionalArguments.Count > 256 || request.AdditionalArguments.Any(argument => argument is null || argument.Length > 32_768 || argument.Contains('\0'))) return Failure("steam.launch.argument_invalid", "A launch argument is invalid.", ErrorKind.Validation);
        return Result.Success();
    }

    public static bool MatchesVariantTarget(GameVariant variant, string target)
    {
        var normalized = target.Replace('\\', '/');
        var suffix = variant switch
        {
            GameVariant.XCom2 => "/Binaries/Win64/XCom2.exe",
            GameVariant.XCom2WarOfTheChosen or GameVariant.XCom2WarOfTheChosenChallengeMode => "/XCom2-WarOfTheChosen/Binaries/Win64/XCom2.exe",
            GameVariant.ChimeraSquad => "/Binaries/Win64/xcom.exe",
            _ => string.Empty
        };
        return normalized.EndsWith(suffix, StringComparison.Ordinal);
    }

    private static Result Failure(string code, string message, ErrorKind kind) => Result.Failure(new Error(code, message, kind));
}

public static class ProtonCommandPlanner
{
    public const string RecursionMarker = "AAML_STEAM_WRAPPER_ACTIVE";

    public static Result<ProtonCommandPlan> Plan(
        SteamLaunchRequest? request,
        IReadOnlyList<string> expandedCommand,
        IReadOnlyDictionary<string, string> inheritedEnvironment,
        string wrapperPath)
    {
        ArgumentNullException.ThrowIfNull(expandedCommand);
        ArgumentNullException.ThrowIfNull(inheritedEnvironment);
        ArgumentException.ThrowIfNullOrWhiteSpace(wrapperPath);
        if (expandedCommand.Count == 0) return Failure("steam.launch.command_empty", "Steam supplied an empty command.", ErrorKind.InvalidData);
        if (inheritedEnvironment.TryGetValue(RecursionMarker, out var active) && active == "1") return Failure("steam.launch.recursive_invocation", "The Steam wrapper was invoked recursively.", ErrorKind.Conflict);

        var environment = new Dictionary<string, string>(inheritedEnvironment, StringComparer.Ordinal) { [RecursionMarker] = "1" };
        if (request is null) return Result<ProtonCommandPlan>.Success(new ProtonCommandPlan(expandedCommand.ToArray(), environment, true));

        var expectedName = request.AppId == SteamAppId.ChimeraSquad ? "xcom.exe" : "XCom2.exe";
        var candidates = expandedCommand.Select((token, index) => (token, index)).Where(pair =>
        {
            var normalized = pair.token.Replace('\\', '/');
            return string.Equals(Path.GetFileName(normalized), expectedName, StringComparison.OrdinalIgnoreCase) && !string.Equals(normalized, wrapperPath.Replace('\\', '/'), StringComparison.Ordinal);
        }).ToArray();
        if (candidates.Length == 0) return Failure("steam.launch.executable_token_not_found", "No recognized game executable token was found.", ErrorKind.InvalidData);
        if (candidates.Length > 1) return Failure("steam.launch.executable_token_ambiguous", "More than one game executable token was found.", ErrorKind.Conflict);

        var tokens = expandedCommand.ToArray();
        tokens[candidates[0].index] = request.TargetExecutablePath;
        var combined = tokens.Concat(request.AdditionalArguments).ToArray();
        return Result<ProtonCommandPlan>.Success(new ProtonCommandPlan(combined, environment, false));
    }

    private static Result<ProtonCommandPlan> Failure(string code, string message, ErrorKind kind) => Result<ProtonCommandPlan>.Failure(new Error(code, message, kind));
}
