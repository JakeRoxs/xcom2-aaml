using AAML.Application.Common;
using AAML.Application.Settings;
using AAML.Application.Startup;
using AAML.Domain.Games;

namespace AAML.Application.Configurations;

public interface IExistingModRootAdoptionService
{
    Task<Result<ApplicationSettings>> ApplyAsync(ExistingModRootPreview preview, IReadOnlySet<int> selectedRows, ApplicationSettings settings, CancellationToken cancellationToken);
}

public interface IExistingModRootPreviewGuard
{
    void Register(ExistingModRootPreview preview);
    void Clear();
    Result EnsureConfigurationSafe(GameVariant variant);
}

public sealed class ExistingModRootPreviewGuard : IExistingModRootPreviewGuard
{
    private GameVariant? blockedVariant;
    private int unresolvedCount;

    public void Register(ExistingModRootPreview preview)
    {
        blockedVariant = preview.Variant;
        unresolvedCount = preview.Rows.Count(row => row.Resolution != ExistingModRootResolution.AlreadyConfigured);
    }

    public void Clear() { blockedVariant = null; unresolvedCount = 0; }

    public Result EnsureConfigurationSafe(GameVariant variant) => blockedVariant == variant && unresolvedCount > 0
        ? Result.Failure(new Error("mod_roots.preview_unconfirmed", $"Review and confirm the existing ModRootDirs preview in Migration before applying configuration ({unresolvedCount} unresolved entr{(unresolvedCount == 1 ? "y" : "ies")}).", ErrorKind.Conflict))
        : Result.Success();
}

public sealed class ExistingModRootAdoptionService(ILegacyGameConfigurationSource source, ISettingsBootstrapper settingsBootstrapper) : IExistingModRootAdoptionService
{
    public async Task<Result<ApplicationSettings>> ApplyAsync(ExistingModRootPreview preview, IReadOnlySet<int> selectedRows, ApplicationSettings settings, CancellationToken cancellationToken)
    {
        if (settings.SelectedGame != preview.Variant || !string.Equals(settings.GameInstallationLocation, preview.InstallationLocation, StringComparison.Ordinal))
            return Result<ApplicationSettings>.Failure(new Error("mod_roots.preview_stale", "The selected game or installation changed after preview.", ErrorKind.Conflict));

        var current = await source.ReadModRootsAsync(preview.Variant, preview.InstallationLocation, settings.ModRootLocations, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<ApplicationSettings>.Failure(current.Error!);
        if (!string.Equals(current.Value!.SourceFingerprint, preview.SourceFingerprint, StringComparison.Ordinal))
            return Result<ApplicationSettings>.Failure(new Error("mod_roots.preview_stale", "XComEngine.ini changed after preview. Preview the roots again.", ErrorKind.Conflict));

        var selected = preview.Rows.Where(row => selectedRows.Contains(row.Index)).ToArray();
        var invalid = selected.FirstOrDefault(row => row.Resolution != ExistingModRootResolution.Valid || row.ResolvedPath is null);
        if (invalid is not null)
            return Result<ApplicationSettings>.Failure(new Error("mod_roots.selection_invalid", $"Only valid roots can be selected: {invalid.RawValue}", ErrorKind.Validation));

        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var roots = settings.ModRootLocations.Concat(selected.Select(row => row.ResolvedPath!)).Distinct(comparison).ToArray();
        return await settingsBootstrapper.SavePreferencesAsync(settings, settings.LaunchArguments, roots, settings.AllowLaunchWithMissingDependencies, settings.CloseAfterLaunch, settings.WorkshopStartupRefresh, settings.Theme, settings.AllowMultipleInstances, settings.CheckForUpdates, settings.UpdateChannel, cancellationToken).ConfigureAwait(false);
    }
}
