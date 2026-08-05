using AAML.Application.Common;
using AAML.Application.Settings;
using AAML.Application.Startup;
using AAML.Domain.Mods;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AAML.Infrastructure.Common.Settings;

/// <summary>Imports the first explicit legacy settings candidate without scanning arbitrary user locations.</summary>
public sealed class LegacySettingsFileImporter(
    IReadOnlyList<string> candidatePaths,
    Func<ModSource, string, Result<string>> normalizeLocation,
    string migrationReportPath) : ILegacySettingsImporter
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
                if (!migrated.IsSuccess) return Result<ApplicationSettings?>.Failure(migrated.Error!);
                try
                {
                    await WriteReportAsync(migrated.Value!.Report, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return Result<ApplicationSettings?>.Failure(new Error("settings.migration_report_write_failed", exception.Message, ErrorKind.Io));
                }
                return Result<ApplicationSettings?>.Success(migrated.Value.Settings);
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

    private async Task WriteReportAsync(LegacySettingsMigrationReport report, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(migrationReportPath);
        if (string.IsNullOrWhiteSpace(directory)) throw new IOException("The legacy migration report path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = migrationReportPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonConvert.SerializeObject(report, Formatting.Indented, new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() });
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, migrationReportPath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
