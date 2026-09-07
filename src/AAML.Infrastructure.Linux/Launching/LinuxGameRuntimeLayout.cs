using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Infrastructure.Common.Steam;
using AAML.Infrastructure.Linux.Paths;

namespace AAML.Infrastructure.Linux.Launching;

/// <summary>Game layout resolved for a specific runtime on Linux.</summary>
public sealed record LinuxGameRuntimeLayout
{
    public LinuxGameRuntimeLayout(
        GameRuntime runtime,
        string gameInstallPath,
        string targetExecutablePath,
        string steamAppsPath,
        string userDataDirectory,
        string configurationDirectory,
        bool usesProtonPrefix,
        string? prefixPath,
        string? wineUser,
        IReadOnlyList<LinuxArtifactCaseFallback> caseFallbacks)
    {
        Runtime = runtime;
        GameInstallPath = gameInstallPath;
        TargetExecutablePath = targetExecutablePath;
        SteamAppsPath = steamAppsPath;
        UserDataDirectory = userDataDirectory;
        ConfigurationDirectory = configurationDirectory;
        UsesProtonPrefix = usesProtonPrefix;
        PrefixPath = prefixPath;
        WineUser = wineUser;
        CaseFallbacks = caseFallbacks;
    }

    public GameRuntime Runtime { get; }
    public string GameInstallPath { get; }
    public string TargetExecutablePath { get; }
    public string SteamAppsPath { get; }
    public string UserDataDirectory { get; }
    public string ConfigurationDirectory { get; }
    public bool UsesProtonPrefix { get; }
    public string? PrefixPath { get; }
    public string? WineUser { get; }
    public IReadOnlyList<LinuxArtifactCaseFallback> CaseFallbacks { get; }

    public static Result<LinuxGameRuntimeLayout> Resolve(string gameInstallPath, GameVariant variant, GameRuntime requestedRuntime)
    {
        try
        {
            var game = Path.GetFullPath(gameInstallPath);
            if (!Directory.Exists(game))
                return Failure("launch.installation_missing", "The selected game installation does not exist.", ErrorKind.NotFound);

            var common = Directory.GetParent(game);
            var steamApps = common?.Parent;
            if (common is null || steamApps is null ||
                !common.Name.Equals("common", StringComparison.OrdinalIgnoreCase) ||
                !steamApps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                return Failure("launch.steam_layout_invalid", "The game installation is not beneath a Steam steamapps/common directory.", ErrorKind.Validation);

            var appId = AAML.Domain.Games.GameVariantPolicy.GetSteamAppId(variant).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var manifest = Path.Combine(steamApps.FullName, $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest))
                return Failure("launch.steam_manifest_missing", "The selected Steam library has no matching application manifest.", ErrorKind.NotFound);

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
            if (installComponents.Length == 0)
                return Failure("launch.steam_manifest_invalid", "The Steam application manifest install directory has no path components.", ErrorKind.InvalidData);
            var manifestInstall = knownArtifacts.ResolveExistingDirectory(common.FullName, installComponents);
            if (!manifestInstall.IsSuccess)
                return KnownArtifactFailure("launch.steam_manifest_mismatch", "The manifest installation could not be resolved", manifestInstall.Error!);
            if (!selectedPhysical.IsSuccess || !string.Equals(selectedPhysical.Value, manifestInstall.Value!.Path, StringComparison.Ordinal))
                return Failure("launch.steam_manifest_mismatch", "The selected installation does not physically match the Steam application manifest.", ErrorKind.Conflict);

            // Detect available layouts
            var hasProtonExecutable = HasWindowsExecutable(knownArtifacts, game, variant);
            var hasNativeExecutable = HasNativeExecutable(knownArtifacts, game, variant);

            if (!hasProtonExecutable && !hasNativeExecutable)
                return Failure("launch.executable_missing", "Neither Proton nor native Linux executable found in installation.", ErrorKind.NotFound);

            // Determine which layout to use based on preference and availability
            var effectiveRuntime = ResolveRuntime(requestedRuntime, hasProtonExecutable, hasNativeExecutable);

            switch (effectiveRuntime)
            {
                case GameRuntime.Proton:
                    return ResolveProtonLayout(knownArtifacts, game, variant, selectedPhysical.Value!, steamApps.FullName);
                case GameRuntime.Native:
                case GameRuntime.Auto:
                    if (hasNativeExecutable)
                        return ResolveNativeLayout(knownArtifacts, game, variant, selectedPhysical.Value!, steamApps.FullName);
                    return ResolveProtonLayout(knownArtifacts, game, variant, selectedPhysical.Value!, steamApps.FullName);
                default:
                    return Failure("launch.runtime_invalid", $"Invalid runtime: {effectiveRuntime}", ErrorKind.Validation);
            }
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

    private static bool HasWindowsExecutable(LinuxKnownArtifactResolver knownArtifacts, string game, GameVariant variant)
    {
        var targetComponents = variant == GameVariant.XCom2
            ? new[] { "Binaries", "Win64", "XCom2.exe" }
            : new[] { "XCom2-WarOfTheChosen", "Binaries", "Win64", "XCom2.exe" };
        var result = knownArtifacts.ResolveExistingFile(game, targetComponents);
        return result.IsSuccess;
    }

    private static bool HasNativeExecutable(LinuxKnownArtifactResolver knownArtifacts, string game, GameVariant variant)
    {
        // Native Feral depot: bin/XCOM2 (Vanilla), bin/XCOM2WOTC (WotC)
        // Alternate legacy paths also checked for compatibility
        if (variant == GameVariant.XCom2)
        {
            var result = knownArtifacts.ResolveExistingFile(game, new[] { "XCOM2", "Binaries", "Linux", "XCOM2" });
            if (result.IsSuccess) return true;
            var result2 = knownArtifacts.ResolveExistingFile(game, new[] { "bin", "XCOM2" });
            return result2.IsSuccess;
        }
        else
        {
            // Actual Feral WotC layout: XCOM2WotC/bin/XCOM2WotC
            var result1 = knownArtifacts.ResolveExistingFile(game, new[] { "XCOM2WotC", "bin", "XCOM2WotC" });
            if (result1.IsSuccess) return true;
            // Alternate legacy paths
            var result2 = knownArtifacts.ResolveExistingFile(game, new[] { "XCom2-WarOfTheChosen", "Binaries", "Linux", "XCOM2WOTC" });
            if (result2.IsSuccess) return true;
            var result3 = knownArtifacts.ResolveExistingFile(game, new[] { "XCom2-WarOfTheChosen", "XCOM2WOTC", "Binaries", "Linux", "XCOM2WOTC" });
            if (result3.IsSuccess) return true;
            var result4 = knownArtifacts.ResolveExistingFile(game, new[] { "XCom2-WarOfTheChosen", "bin", "XCOM2WOTC" });
            return result4.IsSuccess;
        }
    }

    private static GameRuntime ResolveRuntime(GameRuntime requested, bool hasProton, bool hasNative)
    {
        if (requested == GameRuntime.Auto)
            return hasNative ? GameRuntime.Native : GameRuntime.Proton;

        if (requested == GameRuntime.Proton && !hasProton)
            return hasNative ? GameRuntime.Native : GameRuntime.Proton;

        if (requested == GameRuntime.Native && !hasNative)
            return hasProton ? GameRuntime.Proton : GameRuntime.Native;

        return requested;
    }

    private static Result<LinuxGameRuntimeLayout> ResolveProtonLayout(LinuxKnownArtifactResolver knownArtifacts, string game, GameVariant variant, string selectedPhysical, string steamAppsPath)
    {
        var appId = AAML.Domain.Games.GameVariantPolicy.GetSteamAppId(variant).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var prefix = knownArtifacts.ResolveExistingDirectory(steamAppsPath, "compatdata", appId, "pfx");
        if (!prefix.IsSuccess)
            return KnownArtifactFailure("launch.proton_prefix_missing", "The Proton prefix could not be resolved", prefix.Error!);
        var users = knownArtifacts.ResolveExistingDirectory(prefix.Value!.Path, "drive_c", "users");
        if (!users.IsSuccess)
            return KnownArtifactFailure("launch.proton_prefix_missing", "The Proton prefix has no resolvable Windows users directory", users.Error!);
        var candidates = Directory.EnumerateDirectories(users.Value!.Path).Select(Path.GetFileName).Where(name => name is not null && !name.Equals("Public", StringComparison.OrdinalIgnoreCase) && !name.Equals("Default", StringComparison.OrdinalIgnoreCase)).Cast<string>().ToArray();
        var steamUsers = candidates.Where(name => name.Equals("steamuser", StringComparison.OrdinalIgnoreCase)).ToArray();
        var wineUser = steamUsers.Length == 1 ? steamUsers[0] : steamUsers.Length == 0 && candidates.Length == 1 ? candidates[0] : null;
        if (wineUser is null)
            return Failure("launch.proton_user_ambiguous", $"The Proton prefix Windows user could not be selected unambiguously: {string.Join(", ", candidates.Order(StringComparer.Ordinal))}", ErrorKind.Conflict);
        var wineUserPath = knownArtifacts.ResolveExistingDirectory(users.Value.Path, wineUser);
        if (!wineUserPath.IsSuccess)
            return KnownArtifactFailure("launch.proton_user_invalid", "The Proton Windows user directory could not be resolved", wineUserPath.Error!);

        var targetComponents = variant == GameVariant.XCom2
            ? new[] { "Binaries", "Win64", "XCom2.exe" }
            : new[] { "XCom2-WarOfTheChosen", "Binaries", "Win64", "XCom2.exe" };
        var target = knownArtifacts.ResolveExistingFile(game, targetComponents);
        if (!target.IsSuccess)
            return KnownArtifactFailure("launch.executable_missing", "The selected game executable could not be resolved", target.Error!);
        var gameFolder = variant == GameVariant.XCom2 ? "XCOM2" : "XCOM2 War of the Chosen";
        var userData = knownArtifacts.ResolveDirectoryExistingOrExpected(wineUserPath.Value!.Path, "Documents", "My Games", gameFolder);
        if (!userData.IsSuccess)
            return KnownArtifactFailure("launch.user_data_invalid", "The game user-data path could not be resolved", userData.Error!);
        var config = userData.Value!.Exists
            ? knownArtifacts.ResolveDirectoryExistingOrExpected(userData.Value.Path, "XComGame", "Config")
            : Result<LinuxKnownArtifactPath>.Success(new(Path.Combine(userData.Value.Path, "XComGame", "Config"), false, []));
        if (!config.IsSuccess)
            return KnownArtifactFailure("launch.configuration_path_invalid", "The game configuration path could not be resolved", config.Error!);

        var fallbacks = prefix.Value.CaseFallbacks.Concat(users.Value.CaseFallbacks)
            .Concat(wineUserPath.Value.CaseFallbacks).Concat(target.Value!.CaseFallbacks)
            .Concat(userData.Value.CaseFallbacks).Concat(config.Value!.CaseFallbacks).ToArray();

        return Result<LinuxGameRuntimeLayout>.Success(new LinuxGameRuntimeLayout(
            GameRuntime.Proton, selectedPhysical, target.Value.Path, steamAppsPath,
            userData.Value.Path, config.Value.Path,
            true, prefix.Value.Path, wineUser, fallbacks));
    }

    private static Result<LinuxGameRuntimeLayout> ResolveNativeLayout(LinuxKnownArtifactResolver knownArtifacts, string game, GameVariant variant, string selectedPhysical, string steamAppsPath)
    {
        string? executablePath = null;
        var fallbacks = new List<LinuxArtifactCaseFallback>();

        if (variant == GameVariant.XCom2)
        {
            var result = knownArtifacts.ResolveExistingFile(game, new[] { "XCOM2", "Binaries", "Linux", "XCOM2" });
            if (!result.IsSuccess)
                result = knownArtifacts.ResolveExistingFile(game, new[] { "bin", "XCOM2" });
            if (!result.IsSuccess)
                return KnownArtifactFailure("launch.executable_missing", "The native XCOM 2 executable could not be resolved", result.Error!);
            executablePath = result.Value!.Path;
            fallbacks.AddRange(result.Value!.CaseFallbacks);
        }
        else
        {
            // Actual Feral WotC layout: XCOM2WotC/bin/XCOM2WotC
            var result1 = knownArtifacts.ResolveExistingFile(game, new[] { "XCOM2WotC", "bin", "XCOM2WotC" });
            if (result1.IsSuccess)
            {
                executablePath = result1.Value!.Path;
                fallbacks.AddRange(result1.Value!.CaseFallbacks);
            }
            else
            {
                var result2 = knownArtifacts.ResolveExistingFile(game, new[] { "XCom2-WarOfTheChosen", "Binaries", "Linux", "XCOM2WOTC" });
                if (result2.IsSuccess)
                {
                    executablePath = result2.Value!.Path;
                    fallbacks.AddRange(result2.Value!.CaseFallbacks);
                }
                else
                {
                    var result3 = knownArtifacts.ResolveExistingFile(game, new[] { "XCom2-WarOfTheChosen", "XCOM2WOTC", "Binaries", "Linux", "XCOM2WOTC" });
                    if (result3.IsSuccess)
                    {
                        executablePath = result3.Value!.Path;
                        fallbacks.AddRange(result3.Value!.CaseFallbacks);
                    }
                    else
                    {
                        var result4 = knownArtifacts.ResolveExistingFile(game, new[] { "XCom2-WarOfTheChosen", "bin", "XCOM2WOTC" });
                        if (!result4.IsSuccess)
                            return KnownArtifactFailure("launch.executable_missing", "The native WotC executable could not be resolved", result4.Error!);
                        executablePath = result4.Value!.Path;
                        fallbacks.AddRange(result4.Value!.CaseFallbacks);
                    }
                }
            }
        }

        // Native uses in-installation XComGame/Config for both Vanilla and WotC
        // Vanilla: <root>/XCOM2/XComGame/Config
        // WotC: <root>/XCOM2WotC/XComGame/Config (actual Feral layout)
        var gameFolder = variant == GameVariant.XCom2 ? "XCOM2" : "XCOM2WotC";
        var configDir = knownArtifacts.ResolveDirectoryExistingOrExpected(game, gameFolder, "XComGame", "Config");
        if (!configDir.IsSuccess)
            return KnownArtifactFailure("launch.configuration_path_invalid", "The native game configuration path could not be resolved", configDir.Error!);

        var userDataDir = knownArtifacts.ResolveDirectoryExistingOrExpected(game, gameFolder, "XComGame");
        if (!userDataDir.IsSuccess)
            return KnownArtifactFailure("launch.user_data_invalid", "The native game user-data path could not be resolved", userDataDir.Error!);

        fallbacks.AddRange(configDir.Value!.CaseFallbacks);
        fallbacks.AddRange(userDataDir.Value!.CaseFallbacks);

        // Detect Feral VFS user-data tree (separate from in-installation paths)
        var feralUserData = ResolveFeralUserData(game, variant);
        if (feralUserData.IsSuccess)
        {
            // Prefer Feral paths when in-installation user-data is empty
            var inInstallHasData = Directory.Exists(userDataDir.Value.Path) && Directory.EnumerateFiles(userDataDir.Value.Path).Any();
            if (!inInstallHasData)
            {
                fallbacks.Add(new LinuxArtifactCaseFallback(userDataDir.Value.Path, feralUserData.Value!.UserDataPath));
                fallbacks.Add(new LinuxArtifactCaseFallback(configDir.Value.Path, feralUserData.Value!.ConfigPath));
                userDataDir = Result<LinuxKnownArtifactPath>.Success(new LinuxKnownArtifactPath(feralUserData.Value.UserDataPath, true, []));
                configDir = Result<LinuxKnownArtifactPath>.Success(new LinuxKnownArtifactPath(feralUserData.Value.ConfigPath, true, []));
            }
            else
            {
                fallbacks.Add(new LinuxArtifactCaseFallback(userDataDir.Value.Path, feralUserData.Value!.UserDataPath));
                fallbacks.Add(new LinuxArtifactCaseFallback(configDir.Value.Path, feralUserData.Value!.ConfigPath));
            }
        }

        return Result<LinuxGameRuntimeLayout>.Success(new LinuxGameRuntimeLayout(
            GameRuntime.Native, selectedPhysical, executablePath!, steamAppsPath,
            userDataDir.Value!.Path, configDir.Value!.Path,
            false, null, null, fallbacks));
    }

    private static Result<LinuxGameRuntimeLayout> Failure(string code, string message, ErrorKind kind)
        => Result<LinuxGameRuntimeLayout>.Failure(new Error(code, message, kind));

    private static Result<LinuxGameRuntimeLayout> KnownArtifactFailure(string code, string context, Error error)
        => Failure(error.Code is "path.known_artifact_case_ambiguous" or "path.known_artifact_outside_root" ? error.Code : code,
            $"{context}: {error.Message}", error.Kind);

    private static Result<(string UserDataPath, string ConfigPath)> ResolveFeralUserData(string game, GameVariant variant)
    {
        var feralRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "feral-interactive");
        if (!Directory.Exists(feralRoot))
            return Result<(string, string)>.Failure(new Error("launch.feral_user_data_missing", "Feral user-data tree not found.", ErrorKind.NotFound));

        var feralGameDir = variant == GameVariant.XCom2
            ? Path.Combine(feralRoot, "XCOM2")
            : Path.Combine(feralRoot, "XCOM 2 WotC");
        if (!Directory.Exists(feralGameDir))
            return Result<(string, string)>.Failure(new Error("launch.feral_user_data_missing", $"Feral user-data directory not found: {feralGameDir}", ErrorKind.NotFound));

        var vfsLocal = Path.Combine(feralGameDir, "VFS", "Local");
        if (!Directory.Exists(vfsLocal))
            return Result<(string, string)>.Failure(new Error("launch.feral_user_data_missing", $"Feral VFS Local not found: {vfsLocal}", ErrorKind.NotFound));

        var myGames = Path.Combine(vfsLocal, "my games");
        if (!Directory.Exists(myGames))
            return Result<(string, string)>.Failure(new Error("launch.feral_user_data_missing", $"Feral my games not found: {myGames}", ErrorKind.NotFound));

        var xcomGameDir = variant == GameVariant.XCom2
            ? Path.Combine(myGames, "XCOM2", "XComGame")
            : Path.Combine(myGames, "XCOM2 War of the Chosen", "XComGame");
        if (!Directory.Exists(xcomGameDir))
            return Result<(string, string)>.Failure(new Error("launch.feral_user_data_missing", $"Feral XComGame not found: {xcomGameDir}", ErrorKind.NotFound));

        var saveData = Path.Combine(xcomGameDir, "SaveData");
        var configDir = Path.Combine(xcomGameDir, "Config");

        // Only report Feral paths if they contain actual data
        var hasSaveData = Directory.Exists(saveData) && Directory.EnumerateFiles(saveData).Any();
        var hasConfig = Directory.Exists(configDir) && Directory.EnumerateFiles(configDir).Any();
        if (!hasSaveData && !hasConfig)
            return Result<(string, string)>.Failure(new Error("launch.feral_user_data_empty", $"Feral user-data directory exists but is empty: {xcomGameDir}", ErrorKind.NotFound));

        return Result<(string, string)>.Success((xcomGameDir, configDir));
    }
}