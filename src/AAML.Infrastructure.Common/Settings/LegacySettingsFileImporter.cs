using AAML.Application.Common;
using AAML.Application.Settings;
using AAML.Application.Startup;
using AAML.Domain.Mods;

namespace AAML.Infrastructure.Common.Settings;

/// <summary>Imports the first explicit legacy settings candidate without scanning arbitrary user locations.</summary>
public sealed class LegacySettingsFileImporter(
    IReadOnlyList<string> candidatePaths,
    Func<ModSource, string, Result<string>> normalizeLocation) : ILegacySettingsImporter
{
    public async Task<Result<ApplicationSettings?>> TryImportAsync(CancellationToken cancellationToken)
    {
        foreach (var path in candidatePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (cancellationToken.IsCancellationRequested)
                return Result<ApplicationSettings?>.Failure(new Error("settings.migration_cancelled", "Legacy settings migration was cancelled.", ErrorKind.Cancelled));
            if (!File.Exists(path)) continue;
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var migrated = LegacySettingsMigrator.Migrate(json, normalizeLocation);
                return migrated.IsSuccess
                    ? Result<ApplicationSettings?>.Success(migrated.Value)
                    : Result<ApplicationSettings?>.Failure(migrated.Error!);
            }
            catch (OperationCanceledException)
            {
                return Result<ApplicationSettings?>.Failure(new Error("settings.migration_cancelled", "Legacy settings migration was cancelled.", ErrorKind.Cancelled));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Result<ApplicationSettings?>.Failure(new Error("settings.migration_read_failed", exception.Message, ErrorKind.Io));
            }
        }
        return Result<ApplicationSettings?>.Success(null);
    }
}
