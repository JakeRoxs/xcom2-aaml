using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Domain.Games;
using AAML.Domain.Launching;
using AAML.Infrastructure.Common.Configurations;
using AAML.Infrastructure.Common.Steam;
using AAML.Infrastructure.Linux.Paths;
using System.Xml;
using System.Xml.Linq;

namespace AAML.Infrastructure.Linux.Launching;

/// <summary>Writes variant configuration for Proton or native Linux XCOM 2 installations.</summary>
public sealed class LinuxGameConfigurationWriter(IAtomicTextWriter writer) : IGameConfigurationWriter
{
    public async Task<Result<GameConfigurationReceipt>> ApplyAsync(GameLaunchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Variant is not (GameVariant.XCom2 or GameVariant.XCom2WarOfTheChosen))
            return Result<GameConfigurationReceipt>.Failure(new Error("configuration.variant_unsupported", "Linux configuration currently supports XCOM 2 Vanilla and War of the Chosen.", ErrorKind.Validation));

        var layout = LinuxGameRuntimeLayout.Resolve(request.GameInstallationLocation, request.Variant, request.Runtime);
        if (!layout.IsSuccess) return Result<GameConfigurationReceipt>.Failure(layout.Error!);
        var resolved = layout.Value!;

        var configDirectory = resolved.ConfigurationDirectory;
        var modOptionsPath = Path.Combine(configDirectory, "XComModOptions.ini");
        var enginePath = Path.Combine(configDirectory, "XComEngine.ini");
        try
        {
            var modOptions = File.Exists(modOptionsPath) ? await File.ReadAllTextAsync(modOptionsPath, cancellationToken).ConfigureAwait(false) : string.Empty;
            modOptions = UnrealIniUpdater.ReplaceValues(modOptions, "Engine.XComModOptions", "ActiveMods", request.ActiveMods.OrderBy(mod => mod.Order).Select(mod => mod.PackageId.Value));

            var engine = File.Exists(enginePath) ? await File.ReadAllTextAsync(enginePath, cancellationToken).ConfigureAwait(false) : string.Empty;

            // Native: write POSIX paths; Proton: write Wine drive mappings
            var modRoots = resolved.UsesProtonPrefix
                ? request.ModRootLocations.Select(root => ToWinePath(root, resolved.SteamAppsPath).TrimEnd('\\') + "\\").ToArray()
                : request.ModRootLocations.Select(root => root.TrimEnd('/')).ToArray();
            engine = UnrealIniUpdater.ReplaceValues(engine, "Engine.DownloadableContentEnumerator", "ModRootDirs", modRoots);

            var modWrite = await writer.WriteAsync(modOptionsPath, modOptions, cancellationToken).ConfigureAwait(false);
            if (!modWrite.IsSuccess) return Result<GameConfigurationReceipt>.Failure(modWrite.Error!);
            var engineWrite = await writer.WriteAsync(enginePath, engine, cancellationToken).ConfigureAwait(false);
            if (!engineWrite.IsSuccess) return Result<GameConfigurationReceipt>.Failure(engineWrite.Error!);

            var writtenFiles = new List<string> { modOptionsPath, enginePath };
            if (!resolved.UsesProtonPrefix)
            {
                var preferencesPath = ResolveFeralPreferencesPath(request.Variant);
                if (File.Exists(preferencesPath))
                {
                    var preferences = await File.ReadAllTextAsync(preferencesPath, cancellationToken).ConfigureAwait(false);
                    var updatedPreferences = SetFeralDisableAllMods(preferences, request.ActiveMods.Count == 0);
                    if (!string.Equals(preferences, updatedPreferences, StringComparison.Ordinal))
                    {
                        var preferencesWrite = await writer.WriteAsync(preferencesPath, updatedPreferences, cancellationToken).ConfigureAwait(false);
                        if (!preferencesWrite.IsSuccess) return Result<GameConfigurationReceipt>.Failure(preferencesWrite.Error!);
                        writtenFiles.Add(preferencesPath);
                    }
                }
            }

            return Result<GameConfigurationReceipt>.Success(new GameConfigurationReceipt(writtenFiles, request.ActiveMods.OrderBy(mod => mod.Order).Select(mod => mod.PackageId).ToArray(), request.ModRootLocations.ToArray()));
        }
        catch (OperationCanceledException) { return Result<GameConfigurationReceipt>.Failure(new Error("configuration.cancelled", "Game configuration was cancelled.", ErrorKind.Cancelled)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            return Result<GameConfigurationReceipt>.Failure(new Error("configuration.read_failed", exception.Message, ErrorKind.Io));
        }
    }

    private static string ResolveFeralPreferencesPath(GameVariant variant)
    {
        var gameDirectory = variant == GameVariant.XCom2 ? "XCOM2" : "XCOM 2 WotC";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "feral-interactive", gameDirectory, "preferences");
    }

    internal static string SetFeralDisableAllMods(string contents, bool disable)
    {
        var document = XDocument.Parse(contents, LoadOptions.PreserveWhitespace);
        var setting = document.Descendants("value").FirstOrDefault(element =>
            string.Equals((string?)element.Attribute("name"), "DisableAllMods", StringComparison.Ordinal));
        var value = disable ? "1" : "0";
        if (setting is null || setting.Value == value) return contents;

        setting.Value = value;
        var body = document.ToString(SaveOptions.DisableFormatting);
        return document.Declaration is null ? body : $"{document.Declaration}{Environment.NewLine}{body}";
    }

    private static string ToWinePath(string path, string steamAppsPath)
    {
        var normalized = path.TrimEnd('/');
        var steamApps = steamAppsPath.TrimEnd('/');
        if (normalized.StartsWith(steamApps + "/", StringComparison.Ordinal)) return "S:\\" + normalized[(steamApps.Length + 1)..].Replace('/', '\\');
        return normalized.StartsWith("/", StringComparison.Ordinal) ? "Z:" + normalized.Replace('/', '\\') : normalized;
    }
}
