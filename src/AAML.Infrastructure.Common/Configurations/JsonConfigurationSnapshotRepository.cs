using AAML.Application.Common;
using AAML.Application.Configurations;
using AAML.Application.Ports;
using AAML.Domain.Mods;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace AAML.Infrastructure.Common.Configurations;

public sealed class JsonConfigurationSnapshotRepository(IApplicationPaths paths, IAtomicTextWriter writer) : IConfigurationSnapshotRepository
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path = Path.Combine(paths.DataDirectory, "ConfigurationSnapshots", "snapshots.json");
    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        Formatting = Formatting.Indented,
        Converters = [new StringEnumConverter()]
    };

    public async Task<Result<SavedConfigurationSnapshot?>> FindAsync(ConfigurationDocumentId id, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return Result<SavedConfigurationSnapshot?>.Failure(loaded.Error!);
        return Result<SavedConfigurationSnapshot?>.Success(loaded.Value!.SingleOrDefault(snapshot => snapshot.Id == id));
    }

    public async Task<Result> UpsertAsync(SavedConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Cancelled(); }
        try
        {
            var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess) return Result.Failure(loaded.Error!);
            var snapshots = loaded.Value!.Where(existing => existing.Id != snapshot.Id).Append(snapshot).OrderBy(existing => existing.Id.Mod.Source).ThenBy(existing => existing.Id.Mod.LocationIdentity, StringComparer.Ordinal).ThenBy(existing => existing.Id.RelativePath, StringComparer.Ordinal).ToArray();
            return await WriteAsync(snapshots, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<Result> ImportAsync(IReadOnlyList<SavedConfigurationSnapshot> snapshots, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess) return Result.Failure(loaded.Error!);
            var items = loaded.Value!.ToList();
            foreach (var snapshot in snapshots)
            {
                items.RemoveAll(item => item.Id == snapshot.Id);
                items.Add(snapshot);
            }
            return await WriteAsync(items.OrderBy(item => item.Id.Mod.Source).ThenBy(item => item.Id.Mod.LocationIdentity, StringComparer.Ordinal).ThenBy(item => item.Id.RelativePath, StringComparer.Ordinal).ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<Result> RemoveAsync(ConfigurationDocumentId id, CancellationToken cancellationToken)
    {
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Cancelled(); }
        try
        {
            var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess) return Result.Failure(loaded.Error!);
            return await WriteAsync(loaded.Value!.Where(snapshot => snapshot.Id != id).ToArray(), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<Result<IReadOnlyList<SavedConfigurationSnapshot>>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return Result<IReadOnlyList<SavedConfigurationSnapshot>>.Success([]);
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var document = JsonConvert.DeserializeObject<SnapshotDocument>(json, SerializerSettings);
            if (document is null || document.SchemaVersion != 1) return LoadFailure("configuration.snapshot_schema", "Configuration snapshot schema is unsupported.");
            return Result<IReadOnlyList<SavedConfigurationSnapshot>>.Success((document.Snapshots ?? []).Select(item => new SavedConfigurationSnapshot(
                new ConfigurationDocumentId(new ModKey(item.Source, item.LocationIdentity), item.RelativePath), item.Text, new ConfigurationTextFormat(item.Encoding, item.NewLines))).ToArray());
        }
        catch (OperationCanceledException) { return LoadFailure("configuration.snapshot_cancelled", "Configuration snapshot operation was cancelled.", ErrorKind.Cancelled); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return LoadFailure("configuration.snapshot_invalid", exception.Message); }
    }

    private Task<Result> WriteAsync(IReadOnlyList<SavedConfigurationSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var document = new SnapshotDocument(1, snapshots.Select(snapshot => new SnapshotItem(snapshot.Id.Mod.Source, snapshot.Id.Mod.LocationIdentity, snapshot.Id.RelativePath, snapshot.Text, snapshot.Format.Encoding, snapshot.Format.NewLines)).ToArray());
        return writer.WriteAsync(path, JsonConvert.SerializeObject(document, SerializerSettings) + Environment.NewLine, cancellationToken);
    }

    private static Result Cancelled() => Result.Failure(new Error("configuration.snapshot_cancelled", "Configuration snapshot operation was cancelled.", ErrorKind.Cancelled));
    private static Result<IReadOnlyList<SavedConfigurationSnapshot>> LoadFailure(string code, string message, ErrorKind kind = ErrorKind.InvalidData) => Result<IReadOnlyList<SavedConfigurationSnapshot>>.Failure(new Error(code, message, kind));
    private sealed record SnapshotDocument(int SchemaVersion, IReadOnlyList<SnapshotItem> Snapshots);
    private sealed record SnapshotItem(ModSource Source, string LocationIdentity, string RelativePath, string Text, ConfigurationEncoding Encoding, NewLineStyle NewLines);
}
