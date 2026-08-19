using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Settings;
using AAML.Domain.Games;
using AAML.Domain.Launching;

namespace AAML.Application.Startup;

public enum SettingsOrigin { Existing, MigratedLegacy, CreatedDefault }
public sealed record SettingsBootstrapResult(ApplicationSettings Settings, SettingsOrigin Origin);

public interface ILegacySettingsImporter
{
    Task<Result<ApplicationSettings?>> TryImportAsync(CancellationToken cancellationToken);
}

public interface ISettingsBootstrapper
{
    Task<Result<SettingsBootstrapResult>> InitializeAsync(CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> SelectGameAsync(ApplicationSettings settings, GameVariant variant, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> SetGameInstallationAsync(ApplicationSettings settings, string installationPath, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> SetAllowLaunchWithMissingDependenciesAsync(ApplicationSettings settings, bool allow, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> SavePreferencesAsync(ApplicationSettings settings, IReadOnlyList<LaunchArgument> arguments, IReadOnlyList<string> modRoots, bool allowMissingDependencies, bool closeAfterLaunch, WorkshopStartupRefreshPolicy startupRefresh, ThemePreference theme, bool allowMultipleInstances, bool checkForUpdates, UpdateChannelPreference updateChannel, decimal textScale, decimal iconScale, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> SaveModGridPreferencesAsync(ApplicationSettings settings, ModGridPreferences preferences, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> SetNavigationRailModeAsync(ApplicationSettings settings, NavigationRailMode mode, CancellationToken cancellationToken);
    Task<Result<ApplicationSettings>> SetAutoSaveChangesAsync(ApplicationSettings settings, bool enabled, CancellationToken cancellationToken);
}

/// <summary>Loads modern settings, migrates legacy intent once, or creates the minimal default.</summary>
public sealed class SettingsBootstrapper(ISettingsRepository repository, ILegacySettingsImporter legacyImporter) : ISettingsBootstrapper
{
    public async Task<Result<SettingsBootstrapResult>> InitializeAsync(CancellationToken cancellationToken)
    {
        var existing = await repository.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (existing.IsSuccess) return Result<SettingsBootstrapResult>.Success(new SettingsBootstrapResult(existing.Value!, SettingsOrigin.Existing));
        if (existing.Error?.Code != "settings.not_found") return Result<SettingsBootstrapResult>.Failure(existing.Error!);

        var legacy = await legacyImporter.TryImportAsync(cancellationToken).ConfigureAwait(false);
        if (!legacy.IsSuccess) return Result<SettingsBootstrapResult>.Failure(legacy.Error!);
        var settings = legacy.Value ?? CreateDefault();
        var saved = await repository.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess
            ? Result<SettingsBootstrapResult>.Success(new SettingsBootstrapResult(settings, legacy.Value is null ? SettingsOrigin.CreatedDefault : SettingsOrigin.MigratedLegacy))
            : Result<SettingsBootstrapResult>.Failure(saved.Error!);
    }

    public async Task<Result<ApplicationSettings>> SelectGameAsync(ApplicationSettings settings, GameVariant variant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var locations = settings.GameLocations?.ToDictionary() ?? [];
        locations[settings.SelectedGame] = new GameLocationSettings(settings.GameInstallationLocation, settings.ModRootLocations);
        var target = locations.GetValueOrDefault(variant) ?? new GameLocationSettings(null, []);
        var updated = settings with { SelectedGame = variant, GameInstallationLocation = target.InstallationLocation, ModRootLocations = target.ModRootLocations, GameLocations = locations };
        var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ApplicationSettings>.Success(updated) : Result<ApplicationSettings>.Failure(saved.Error!);
    }

    public async Task<Result<ApplicationSettings>> SetGameInstallationAsync(ApplicationSettings settings, string installationPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(installationPath))
            return Result<ApplicationSettings>.Failure(new Error("settings.game_path_required", "A game installation path is required.", ErrorKind.Validation));
        var locations = settings.GameLocations?.ToDictionary() ?? [];
        var active = settings.LocationFor(settings.SelectedGame);
        locations[settings.SelectedGame] = active with { InstallationLocation = installationPath.Trim() };
        var updated = settings with { GameInstallationLocation = installationPath.Trim(), GameLocations = locations };
        var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ApplicationSettings>.Success(updated) : Result<ApplicationSettings>.Failure(saved.Error!);
    }

    public async Task<Result<ApplicationSettings>> SetAllowLaunchWithMissingDependenciesAsync(ApplicationSettings settings, bool allow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var updated = settings with { AllowLaunchWithMissingDependencies = allow };
        var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ApplicationSettings>.Success(updated) : Result<ApplicationSettings>.Failure(saved.Error!);
    }

    public async Task<Result<ApplicationSettings>> SavePreferencesAsync(ApplicationSettings settings, IReadOnlyList<LaunchArgument> arguments, IReadOnlyList<string> modRoots, bool allowMissingDependencies, bool closeAfterLaunch, WorkshopStartupRefreshPolicy startupRefresh, ThemePreference theme, bool allowMultipleInstances, bool checkForUpdates, UpdateChannelPreference updateChannel, decimal textScale, decimal iconScale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var roots = modRoots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => Path.GetFullPath(root.Trim())).Distinct(comparison).ToArray();
            var missing = roots.FirstOrDefault(root => !Directory.Exists(root));
            if (missing is not null) return Result<ApplicationSettings>.Failure(new Error("settings.mod_root_missing", $"Mod root does not exist: {missing}", ErrorKind.NotFound));
            if (!ApplicationSettingsDefaults.IsTextScaleSupported(textScale)) return Result<ApplicationSettings>.Failure(new Error("settings.text_scale_invalid", $"Text scale must be between {ApplicationSettingsDefaults.MinimumTextScale} and {ApplicationSettingsDefaults.MaximumTextScale}.", ErrorKind.Validation));
            if (!ApplicationSettingsDefaults.IsIconScaleSupported(iconScale)) return Result<ApplicationSettings>.Failure(new Error("settings.icon_scale_invalid", $"Icon scale must be between {ApplicationSettingsDefaults.MinimumIconScale} and {ApplicationSettingsDefaults.MaximumIconScale}.", ErrorKind.Validation));
            var locations = settings.GameLocations?.ToDictionary() ?? [];
            locations[settings.SelectedGame] = new GameLocationSettings(settings.GameInstallationLocation, roots);
            var updated = settings with { LaunchArguments = arguments.ToArray(), ModRootLocations = roots, GameLocations = locations, AllowLaunchWithMissingDependencies = allowMissingDependencies, CloseAfterLaunch = closeAfterLaunch, WorkshopStartupRefresh = startupRefresh, Theme = theme, AllowMultipleInstances = allowMultipleInstances, CheckForUpdates = checkForUpdates, UpdateChannel = updateChannel, TextScale = textScale, IconScale = iconScale };
            var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return saved.IsSuccess ? Result<ApplicationSettings>.Success(updated) : Result<ApplicationSettings>.Failure(saved.Error!);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<ApplicationSettings>.Failure(new Error("settings.mod_root_invalid", exception.Message, ErrorKind.Validation));
        }
    }

    public async Task<Result<ApplicationSettings>> SaveModGridPreferencesAsync(ApplicationSettings settings, ModGridPreferences preferences, CancellationToken cancellationToken)
    {
        var updated = settings with { ModGrid = preferences };
        var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ApplicationSettings>.Success(updated) : Result<ApplicationSettings>.Failure(saved.Error!);
    }

    public async Task<Result<ApplicationSettings>> SetNavigationRailModeAsync(ApplicationSettings settings, NavigationRailMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var updated = settings with { NavigationRailMode = mode };
        var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ApplicationSettings>.Success(updated) : Result<ApplicationSettings>.Failure(saved.Error!);
    }

    public async Task<Result<ApplicationSettings>> SetAutoSaveChangesAsync(ApplicationSettings settings, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var updated = settings with { AutoSaveChanges = enabled };
        var saved = await repository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Result<ApplicationSettings>.Success(updated) : Result<ApplicationSettings>.Failure(saved.Error!);
    }

    private static ApplicationSettings CreateDefault() => new(
        ApplicationSettingsDefaults.CurrentSchemaVersion, GameVariant.XCom2, null, [], ApplicationSettingsDefaults.LaunchArguments, [], [], [], false);
}
