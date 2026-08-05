using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Infrastructure.Common.Configurations;

namespace AAML.Infrastructure.Linux.Launching;

/// <summary>Writes variant configuration inside the Proton prefix associated with the selected Steam library.</summary>
public sealed class LinuxGameConfigurationWriter(IAtomicTextWriter writer) : IGameConfigurationWriter
{
    public async Task<Result<GameConfigurationReceipt>> ApplyAsync(GameLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Variant is not (GameVariant.XCom2 or GameVariant.XCom2WarOfTheChosen))
            return Result<GameConfigurationReceipt>.Failure(new Error("configuration.variant_unsupported", "Linux configuration currently supports XCOM 2 Vanilla and War of the Chosen.", ErrorKind.Validation));
        var layout = LinuxSteamGameLayout.Resolve(request.GameInstallationLocation, request.Variant);
        if (!layout.IsSuccess) return Result<GameConfigurationReceipt>.Failure(layout.Error!);
        var configDirectory = layout.Value!.ConfigurationDirectory;
        var modOptionsPath = Path.Combine(configDirectory, "XComModOptions.ini");
        var enginePath = Path.Combine(configDirectory, "XComEngine.ini");
        try
        {
            var modOptions = File.Exists(modOptionsPath) ? await File.ReadAllTextAsync(modOptionsPath, cancellationToken).ConfigureAwait(false) : string.Empty;
            modOptions = UnrealIniUpdater.ReplaceValues(modOptions, "Engine.XComModOptions", "ActiveMods", request.ActiveMods.OrderBy(mod => mod.Order).Select(mod => mod.PackageId.Value));
            var engine = File.Exists(enginePath) ? await File.ReadAllTextAsync(enginePath, cancellationToken).ConfigureAwait(false) : string.Empty;
            engine = UnrealIniUpdater.ReplaceValues(engine, "Engine.DownloadableContentEnumerator", "ModRootDirs", request.ModRootLocations.Select(root => ToWinePath(root, layout.Value.SteamAppsPath).TrimEnd('\\') + "\\"));
            var modWrite = await writer.WriteAsync(modOptionsPath, modOptions, cancellationToken).ConfigureAwait(false);
            if (!modWrite.IsSuccess) return Result<GameConfigurationReceipt>.Failure(modWrite.Error!);
            var engineWrite = await writer.WriteAsync(enginePath, engine, cancellationToken).ConfigureAwait(false);
            return engineWrite.IsSuccess
                ? Result<GameConfigurationReceipt>.Success(new GameConfigurationReceipt([modOptionsPath, enginePath], request.ActiveMods.OrderBy(mod => mod.Order).Select(mod => mod.PackageId).ToArray(), request.ModRootLocations.ToArray()))
                : Result<GameConfigurationReceipt>.Failure(engineWrite.Error!);
        }
        catch (OperationCanceledException) { return Result<GameConfigurationReceipt>.Failure(new Error("configuration.cancelled", "Game configuration was cancelled.", ErrorKind.Cancelled)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<GameConfigurationReceipt>.Failure(new Error("configuration.read_failed", exception.Message, ErrorKind.Io));
        }
    }

    private static string ToWinePath(string path, string steamAppsPath)
    {
        var normalized = path.TrimEnd('/');
        var steamApps = steamAppsPath.TrimEnd('/');
        if (normalized.StartsWith(steamApps + "/", StringComparison.Ordinal)) return "S:\\" + normalized[(steamApps.Length + 1)..].Replace('/', '\\');
        return normalized.StartsWith("/", StringComparison.Ordinal) ? "Z:" + normalized.Replace('/', '\\') : normalized;
    }
}

internal sealed record LinuxSteamGameLayout(string GameInstallPath, string TargetExecutablePath, string SteamAppsPath, string PrefixPath, string WineUser, string ConfigurationDirectory)
{
    public static Result<LinuxSteamGameLayout> Resolve(string gameInstallPath, GameVariant variant)
    {
        try
        {
            var game = Path.GetFullPath(gameInstallPath);
            if (!Directory.Exists(game)) return Failure("launch.installation_missing", "The selected game installation does not exist.", ErrorKind.NotFound);
            var common = Directory.GetParent(game);
            var steamApps = common?.Parent;
            if (common is null || steamApps is null || !common.Name.Equals("common", StringComparison.Ordinal) || !steamApps.Name.Equals("steamapps", StringComparison.Ordinal))
                return Failure("launch.steam_layout_invalid", "The game installation is not beneath a Steam steamapps/common directory.", ErrorKind.Validation);
            var appId = AAML.Domain.Games.GameVariantPolicy.GetSteamAppId(variant).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var prefix = Path.Combine(steamApps.FullName, "compatdata", appId, "pfx");
            var users = Path.Combine(prefix, "drive_c", "users");
            if (!Directory.Exists(users)) return Failure("launch.proton_prefix_missing", "The Proton prefix has no Windows users directory.", ErrorKind.NotFound);
            var candidates = Directory.EnumerateDirectories(users).Select(Path.GetFileName).Where(name => name is not null && !name.Equals("Public", StringComparison.OrdinalIgnoreCase) && !name.Equals("Default", StringComparison.OrdinalIgnoreCase)).Cast<string>().ToArray();
            var wineUser = candidates.FirstOrDefault(name => name.Equals("steamuser", StringComparison.OrdinalIgnoreCase)) ?? (candidates.Length == 1 ? candidates[0] : null);
            if (wineUser is null) return Failure("launch.proton_user_ambiguous", "The Proton prefix Windows user could not be selected unambiguously.", ErrorKind.Conflict);
            var variantRoot = variant == GameVariant.XCom2 ? game : Path.Combine(game, "XCom2-WarOfTheChosen");
            var target = Path.Combine(variantRoot, "Binaries", "Win64", "XCom2.exe");
            if (!File.Exists(target)) return Failure("launch.executable_missing", $"The selected game executable does not exist: {target}", ErrorKind.NotFound);
            var gameFolder = variant == GameVariant.XCom2 ? "XCOM2" : "XCOM2 War of the Chosen";
            var config = Path.Combine(users, wineUser, "Documents", "My Games", gameFolder, "XComGame", "Config");
            return Result<LinuxSteamGameLayout>.Success(new LinuxSteamGameLayout(game, target, steamApps.FullName, prefix, wineUser, config));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failure("launch.path_invalid", exception.Message, ErrorKind.Validation);
        }
    }

    private static Result<LinuxSteamGameLayout> Failure(string code, string message, ErrorKind kind) => Result<LinuxSteamGameLayout>.Failure(new Error(code, message, kind));
}
