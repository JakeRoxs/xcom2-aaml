using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Infrastructure.Common.Configurations;
using AAML.Infrastructure.Common.Steam;
using AAML.Infrastructure.Linux.Paths;

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

internal sealed record LinuxSteamGameLayout(string GameInstallPath, string TargetExecutablePath, string SteamAppsPath, string PrefixPath, string WineUser, string UserDataDirectory, string ConfigurationDirectory, IReadOnlyList<LinuxArtifactCaseFallback> CaseFallbacks)
{
    public static Result<LinuxSteamGameLayout> Resolve(string gameInstallPath, GameVariant variant)
    {
        try
        {
            var game = Path.GetFullPath(gameInstallPath);
            if (!Directory.Exists(game)) return Failure("launch.installation_missing", "The selected game installation does not exist.", ErrorKind.NotFound);
            var common = Directory.GetParent(game);
            var steamApps = common?.Parent;
            if (common is null || steamApps is null || !common.Name.Equals("common", StringComparison.OrdinalIgnoreCase) || !steamApps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                return Failure("launch.steam_layout_invalid", "The game installation is not beneath a Steam steamapps/common directory.", ErrorKind.Validation);
            var appId = AAML.Domain.Games.GameVariantPolicy.GetSteamAppId(variant).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var manifest = Path.Combine(steamApps.FullName, $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) return Failure("launch.steam_manifest_missing", "The selected Steam library has no matching application manifest.", ErrorKind.NotFound);
            var fields = ValveKeyValueParser.Parse(File.ReadAllText(manifest)).SelectMany(entry => entry.Children)
                .Where(entry => entry.Value is not null).GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Value!, StringComparer.OrdinalIgnoreCase);
            if (fields.GetValueOrDefault("appid") != appId || fields.GetValueOrDefault("installdir") is not { } installDirectory ||
                string.IsNullOrWhiteSpace(installDirectory) || Path.IsPathRooted(installDirectory) || installDirectory.Contains("..", StringComparison.Ordinal))
                return Failure("launch.steam_manifest_invalid", "The Steam application manifest does not identify a safe matching installation.", ErrorKind.InvalidData);
            var physical = new LinuxPhysicalPathResolver();
            var knownArtifacts = new LinuxKnownArtifactResolver(physical);
            var selectedPhysical = physical.ResolveExisting(game);
            var installComponents = installDirectory.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (installComponents.Length == 0) return Failure("launch.steam_manifest_invalid", "The Steam application manifest install directory has no path components.", ErrorKind.InvalidData);
            var manifestInstall = knownArtifacts.ResolveExistingDirectory(common.FullName, installComponents);
            if (!manifestInstall.IsSuccess) return KnownArtifactFailure("launch.steam_manifest_mismatch", "The manifest installation could not be resolved", manifestInstall.Error!);
            if (!selectedPhysical.IsSuccess || !string.Equals(selectedPhysical.Value, manifestInstall.Value!.Path, StringComparison.Ordinal))
                return Failure("launch.steam_manifest_mismatch", "The selected installation does not physically match the Steam application manifest.", ErrorKind.Conflict);
            var prefix = knownArtifacts.ResolveExistingDirectory(steamApps.FullName, "compatdata", appId, "pfx");
            if (!prefix.IsSuccess) return KnownArtifactFailure("launch.proton_prefix_missing", "The Proton prefix could not be resolved", prefix.Error!);
            var users = knownArtifacts.ResolveExistingDirectory(prefix.Value!.Path, "drive_c", "users");
            if (!users.IsSuccess) return KnownArtifactFailure("launch.proton_prefix_missing", "The Proton prefix has no resolvable Windows users directory", users.Error!);
            var candidates = Directory.EnumerateDirectories(users.Value!.Path).Select(Path.GetFileName).Where(name => name is not null && !name.Equals("Public", StringComparison.OrdinalIgnoreCase) && !name.Equals("Default", StringComparison.OrdinalIgnoreCase)).Cast<string>().ToArray();
            var steamUsers = candidates.Where(name => name.Equals("steamuser", StringComparison.OrdinalIgnoreCase)).ToArray();
            var wineUser = steamUsers.Length == 1 ? steamUsers[0] : steamUsers.Length == 0 && candidates.Length == 1 ? candidates[0] : null;
            if (wineUser is null) return Failure("launch.proton_user_ambiguous", $"The Proton prefix Windows user could not be selected unambiguously: {string.Join(", ", candidates.Order(StringComparer.Ordinal))}", ErrorKind.Conflict);
            var wineUserPath = knownArtifacts.ResolveExistingDirectory(users.Value.Path, wineUser);
            if (!wineUserPath.IsSuccess) return KnownArtifactFailure("launch.proton_user_invalid", "The Proton Windows user directory could not be resolved", wineUserPath.Error!);
            var targetComponents = variant == GameVariant.XCom2
                ? new[] { "Binaries", "Win64", "XCom2.exe" }
                : ["XCom2-WarOfTheChosen", "Binaries", "Win64", "XCom2.exe"];
            var target = knownArtifacts.ResolveExistingFile(game, targetComponents);
            if (!target.IsSuccess) return KnownArtifactFailure("launch.executable_missing", "The selected game executable could not be resolved", target.Error!);
            var gameFolder = variant == GameVariant.XCom2 ? "XCOM2" : "XCOM2 War of the Chosen";
            var userData = knownArtifacts.ResolveDirectoryExistingOrExpected(wineUserPath.Value!.Path, "Documents", "My Games", gameFolder);
            if (!userData.IsSuccess) return KnownArtifactFailure("launch.user_data_invalid", "The game user-data path could not be resolved", userData.Error!);
            var config = userData.Value!.Exists
                ? knownArtifacts.ResolveDirectoryExistingOrExpected(userData.Value.Path, "XComGame", "Config")
                : Result<LinuxKnownArtifactPath>.Success(new(Path.Combine(userData.Value.Path, "XComGame", "Config"), false, []));
            if (!config.IsSuccess) return KnownArtifactFailure("launch.configuration_path_invalid", "The game configuration path could not be resolved", config.Error!);
            var fallbacks = manifestInstall.Value.CaseFallbacks.Concat(prefix.Value.CaseFallbacks).Concat(users.Value.CaseFallbacks).Concat(wineUserPath.Value.CaseFallbacks)
                .Concat(target.Value!.CaseFallbacks).Concat(userData.Value.CaseFallbacks).Concat(config.Value!.CaseFallbacks).ToArray();
            return Result<LinuxSteamGameLayout>.Success(new LinuxSteamGameLayout(selectedPhysical.Value!, target.Value.Path, steamApps.FullName,
                prefix.Value.Path, wineUser, userData.Value.Path, config.Value.Path, fallbacks));
        }
        catch (FormatException exception)
        {
            return Failure("launch.steam_manifest_invalid", $"The Steam application manifest is malformed: {exception.Message}", ErrorKind.InvalidData);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failure("launch.path_invalid", exception.Message, ErrorKind.Validation);
        }
    }

    private static Result<LinuxSteamGameLayout> Failure(string code, string message, ErrorKind kind) => Result<LinuxSteamGameLayout>.Failure(new Error(code, message, kind));
    private static Result<LinuxSteamGameLayout> KnownArtifactFailure(string missingCode, string context, Error error) =>
        Failure(error.Code is "path.known_artifact_case_ambiguous" or "path.known_artifact_outside_root" ? error.Code : missingCode,
            $"{context}: {error.Message}", error.Kind);
}
