using System.Text;
using AAML.Application.Common;
using AAML.Application.Ports;
using AAML.Application.Settings;
using Newtonsoft.Json;

namespace AAML.Infrastructure.Common.Settings;

/// <summary>Reports whether a load upgraded and durably canonicalized a settings document.</summary>
public sealed record SettingsLoadReport(int SourceSchemaVersion, bool CanonicalRewriteAttempted, bool CanonicalRewriteSucceeded, Error? RewriteError = null);

/// <summary>Persists versioned settings atomically in the explicit configuration directory.</summary>
public sealed class JsonSettingsRepository(IApplicationPaths paths) : ISettingsRepository
{
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private string SettingsPath => Path.Combine(paths.ConfigurationDirectory, "settings.json");
    public SettingsLoadReport? LastLoadReport { get; private set; }

    public async Task<Result<ApplicationSettings>> LoadAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Result<ApplicationSettings>.Failure(new Error("settings.cancelled", "The settings read was cancelled.", ErrorKind.Cancelled));
        LastLoadReport = null;
        var primary = await ReadAsync(SettingsPath, cancellationToken).ConfigureAwait(false);
        if (primary.IsSuccess)
        {
            return await FinishLoadAsync(primary.Value!, allowRewrite: true, cancellationToken).ConfigureAwait(false);
        }
        if (primary.Error?.Kind == ErrorKind.Cancelled) return Result<ApplicationSettings>.Failure(primary.Error);

        var backup = await ReadAsync(SettingsPath + ".bak", cancellationToken).ConfigureAwait(false);
        if (backup.Error?.Kind == ErrorKind.Cancelled) return Result<ApplicationSettings>.Failure(backup.Error);
        if (primary.Error?.Kind == ErrorKind.NotFound && backup.Error?.Kind == ErrorKind.NotFound)
            return Result<ApplicationSettings>.Failure(new Error("settings.not_found", "No modern settings document exists.", ErrorKind.NotFound));
        return backup.IsSuccess
            ? await FinishLoadAsync(backup.Value!, allowRewrite: false, cancellationToken).ConfigureAwait(false)
            : Result<ApplicationSettings>.Failure(new Error("settings.recovery_failed", "Neither primary nor backup settings could be loaded.", ErrorKind.Io));
    }

    public async Task<Result> SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var lockAcquired = false;
        try
        {
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockAcquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(paths.ConfigurationDirectory);
            var json = JsonConvert.SerializeObject(ApplicationSettingsMapper.ToDocument(settings), Formatting.Indented);
            var temporary = Path.Combine(paths.ConfigurationDirectory, $".settings.{Guid.NewGuid():N}.tmp");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(json);
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(SettingsPath))
                {
                    File.Replace(temporary, SettingsPath, SettingsPath + ".bak");
                }
                else
                {
                    File.Move(temporary, SettingsPath);
                }

                return Result.Success();
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(new Error("settings.cancelled", "The settings write was cancelled.", ErrorKind.Cancelled));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(new Error("settings.write_failed", exception.Message, ErrorKind.Io));
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return Result.Failure(new Error("settings.invalid", exception.Message, ErrorKind.InvalidData));
        }
        finally
        {
            if (lockAcquired) writeLock.Release();
        }
    }

    private async Task<Result<ApplicationSettings>> FinishLoadAsync(SettingsReadResult read, bool allowRewrite, CancellationToken cancellationToken)
    {
        if (!read.RequiresCanonicalRewrite || !allowRewrite)
        {
            LastLoadReport = new SettingsLoadReport(read.SourceSchemaVersion, false, false);
            return Result<ApplicationSettings>.Success(read.Settings);
        }

        var rewrite = await SaveAsync(read.Settings, cancellationToken).ConfigureAwait(false);
        LastLoadReport = new SettingsLoadReport(read.SourceSchemaVersion, true, rewrite.IsSuccess, rewrite.Error);
        return Result<ApplicationSettings>.Success(read.Settings);
    }

    private static async Task<Result<SettingsReadResult>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Result<SettingsReadResult>.Failure(new Error("settings.not_found", "Settings were not found.", ErrorKind.NotFound));
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return Result<SettingsReadResult>.Success(SettingsSchemaMigrator.Read(json));
        }
        catch (OperationCanceledException)
        {
            return Result<SettingsReadResult>.Failure(new Error("settings.cancelled", "The settings read was cancelled.", ErrorKind.Cancelled));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            return Result<SettingsReadResult>.Failure(new Error("settings.invalid", exception.Message, ErrorKind.InvalidData));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result<SettingsReadResult>.Failure(new Error("settings.read_failed", exception.Message, ErrorKind.Io));
        }
    }
}
