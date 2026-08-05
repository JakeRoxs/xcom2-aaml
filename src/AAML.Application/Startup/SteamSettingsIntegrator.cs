using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Application.Steam;
using AAML.Domain.Games;

namespace AAML.Application.Startup;

public interface ISteamSettingsIntegrator
{
    Task<Result<SteamSettingsIntegration>> DiscoverAndApplyAsync(ApplicationSettings settings, CancellationToken cancellationToken);
}

public sealed record SteamSettingsIntegration(ApplicationSettings Settings, SteamGameDiscovery Discovery);

/// <summary>Persists only an unambiguous installed game and its existing Workshop roots.</summary>
public sealed class SteamSettingsIntegrator(ISteamFilesystemDiscovery discovery, ISettingsRepository repository) : ISteamSettingsIntegrator
{
    public async Task<Result<SteamSettingsIntegration>> DiscoverAndApplyAsync(ApplicationSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var appId = new SteamAppId(GameVariantPolicy.GetSteamAppId(settings.SelectedGame));
        var discovered = await discovery.DiscoverAsync(new SteamDiscoveryRequest([appId]), cancellationToken).ConfigureAwait(false);
        if (!discovered.IsSuccess) return Result<SteamSettingsIntegration>.Failure(discovered.Error!);
        var installed = discovered.Value!.Applications.Where(application => application.InstallDirectoryExists).ToArray();
        if (installed.Length == 0) return Result<SteamSettingsIntegration>.Failure(new Error("steam.game_install_missing", "Steam did not identify an installed copy of the selected game.", ErrorKind.NotFound));
        if (installed.Length > 1) return Result<SteamSettingsIntegration>.Failure(new Error("steam.game_install_ambiguous", "Steam identified more than one installed copy of the selected game.", ErrorKind.Conflict));
        var selected = installed[0];
        var workshopRoots = discovered.Value.WorkshopLocations
            .Where(location => string.Equals(location.LibraryRootPath, selected.LibraryRootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .Select(location => location.ContentRootPath)
            .Where(Directory.Exists);
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = settings.ModRootLocations.Where(root => !IsSteamWorkshopContentRoot(root)).Concat(workshopRoots).Distinct(comparison).ToArray();
        var locations = settings.GameLocations?.ToDictionary() ?? [];
        locations[settings.SelectedGame] = new GameLocationSettings(selected.GameInstallPath, roots);
        var updated = settings with { GameInstallationLocation = selected.GameInstallPath, ModRootLocations = roots, GameLocations = locations };
        var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess
            ? Result<SteamSettingsIntegration>.Success(new SteamSettingsIntegration(updated, discovered.Value))
            : Result<SteamSettingsIntegration>.Failure(saved.Error!);
    }

    private static bool IsSteamWorkshopContentRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var components = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return components.Length >= 3 && components[^2].Equals("content", StringComparison.OrdinalIgnoreCase) && components[^3].Equals("workshop", StringComparison.OrdinalIgnoreCase) && uint.TryParse(components[^1], out _);
    }
}
