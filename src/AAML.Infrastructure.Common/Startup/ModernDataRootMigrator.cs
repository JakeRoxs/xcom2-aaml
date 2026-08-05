using System.Security.Cryptography;
using AAML.Application.Ports;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace AAML.Infrastructure.Common.Startup;

public enum DataRootMigrationStatus { Started, Completed, CompletedWithConflicts, Failed }
public enum DataRootMigrationOutcome { Copied, SourceMissing, DestinationOnly, AlreadyPresent, Conflict, Failed }

public sealed record DataRootIdentity(
    string ConfigurationDirectory,
    string DataDirectory,
    string StateDirectory);

public sealed record DataRootMigrationItem(
    string Id,
    string Source,
    string Destination,
    bool Durable,
    DataRootMigrationOutcome Outcome,
    string? Sha256,
    long? Length,
    string? Message,
    DateTimeOffset CompletedAtUtc);

public sealed record DataRootMigrationReceipt(
    int SchemaVersion,
    string MigrationId,
    int ExpectedManifestVersion,
    int ExpectedManifestCount,
    DataRootIdentity SourceRoot,
    DataRootIdentity CurrentRoot,
    DataRootMigrationStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<DataRootMigrationItem> Items);

internal sealed record DataRootMigrationTestHooks(Action<int>? AfterItemPersisted = null);

public static class ModernDataRootMigrator
{
    private const int ReceiptSchemaVersion = 2;
    private const int ManifestVersion = 1;
    private const string MigrationId = "modern-data-root-v1";
    private const string ReceiptFileName = MigrationId + ".json";
    private const string LegacyReceiptArchiveFileName = MigrationId + ".receipt-schema-v1.json";

    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Formatting = Formatting.Indented,
        Converters = { new StringEnumConverter() }
    };

    private static readonly (string Id, Func<IApplicationPaths, string> Root, Func<IApplicationPaths, string> Path, bool Durable)[] Manifest =
    [
        ("settings", paths => paths.ConfigurationDirectory, paths => Path.Combine(paths.ConfigurationDirectory, "settings.json"), true),
        ("settings-backup", paths => paths.ConfigurationDirectory, paths => Path.Combine(paths.ConfigurationDirectory, "settings.json.bak"), true),
        ("profiles", paths => paths.DataDirectory, paths => Path.Combine(paths.DataDirectory, "Profiles", "profiles.json"), true),
        ("profiles-backup", paths => paths.DataDirectory, paths => Path.Combine(paths.DataDirectory, "Profiles", "profiles.json.bak"), true),
        ("snapshots", paths => paths.DataDirectory, paths => Path.Combine(paths.DataDirectory, "ConfigurationSnapshots", "snapshots.json"), true),
        ("snapshots-backup", paths => paths.DataDirectory, paths => Path.Combine(paths.DataDirectory, "ConfigurationSnapshots", "snapshots.json.bak"), true),
        ("log", paths => paths.StateDirectory, paths => Path.Combine(paths.StateDirectory, "Logs", "aaml.log"), false),
        ("log-1", paths => paths.StateDirectory, paths => Path.Combine(paths.StateDirectory, "Logs", "aaml.log.1"), false),
        ("log-2", paths => paths.StateDirectory, paths => Path.Combine(paths.StateDirectory, "Logs", "aaml.log.2"), false),
        ("log-3", paths => paths.StateDirectory, paths => Path.Combine(paths.StateDirectory, "Logs", "aaml.log.3"), false),
        ("log-4", paths => paths.StateDirectory, paths => Path.Combine(paths.StateDirectory, "Logs", "aaml.log.4"), false),
        ("log-5", paths => paths.StateDirectory, paths => Path.Combine(paths.StateDirectory, "Logs", "aaml.log.5"), false)
    ];

    public static DataRootMigrationReceipt Migrate(IApplicationPaths former, IApplicationPaths current, CancellationToken cancellationToken) =>
        Migrate(former, current, cancellationToken, null);

    internal static DataRootMigrationReceipt Migrate(
        IApplicationPaths former,
        IApplicationPaths current,
        CancellationToken cancellationToken,
        DataRootMigrationTestHooks? testHooks)
    {
        ArgumentNullException.ThrowIfNull(former);
        ArgumentNullException.ThrowIfNull(current);

        var migrationDirectory = Path.Combine(current.StateDirectory, "Migrations");
        Directory.CreateDirectory(migrationDirectory);
        EnsureSafeDirectory(current.StateDirectory, current.StateDirectory);
        EnsureSafeDirectory(migrationDirectory, current.StateDirectory);

        var lockPath = Path.Combine(migrationDirectory, MigrationId + ".lock");
        using var migrationLock = AcquireLock(lockPath);
        var receiptPath = Path.Combine(migrationDirectory, ReceiptFileName);
        var sourceRoot = Identify(former);
        var currentRoot = Identify(current);
        var receipt = ReadReceipt(receiptPath, migrationDirectory, former, current, sourceRoot, currentRoot)
            ?? CreateStartedReceipt(sourceRoot, currentRoot);

        if (receipt.Items.Count == 0 && !File.Exists(receiptPath)) WriteReceipt(migrationDirectory, receipt);

        if (receipt.Status is DataRootMigrationStatus.Completed or DataRootMigrationStatus.CompletedWithConflicts)
        {
            var verified = VerifyCompletedCopies(receipt, former, current, cancellationToken);
            if (!ReferenceEquals(verified, receipt)) WriteReceipt(migrationDirectory, verified);
            return verified;
        }

        var items = receipt.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var entry in Manifest)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (items.TryGetValue(entry.Id, out var existing) && existing.Outcome != DataRootMigrationOutcome.Failed) continue;

            var source = entry.Path(former);
            var destination = entry.Path(current);
            DataRootMigrationItem item;
            try
            {
                item = Process(entry.Id, source, entry.Root(former), destination, entry.Root(current), entry.Durable, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                item = new(entry.Id, source, destination, entry.Durable, DataRootMigrationOutcome.Failed, null, null, exception.Message, DateTimeOffset.UtcNow);
            }

            items[entry.Id] = item;
            receipt = BuildProgress(receipt, items.Values, completed: false);
            WriteReceipt(migrationDirectory, receipt);
            testHooks?.AfterItemPersisted?.Invoke(receipt.Items.Count);
        }

        receipt = BuildProgress(receipt, items.Values, completed: true);
        WriteReceipt(migrationDirectory, receipt);
        return receipt;
    }

    private static FileStream AcquireLock(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new IOException("The modern data-root migration is already running; startup was stopped without changing migration state.", exception);
        }
    }

    private static DataRootMigrationReceipt? ReadReceipt(
        string receiptPath,
        string migrationDirectory,
        IApplicationPaths former,
        IApplicationPaths current,
        DataRootIdentity sourceRoot,
        DataRootIdentity currentRoot)
    {
        if (!File.Exists(receiptPath)) return null;

        string json;
        JObject document;
        try
        {
            json = File.ReadAllText(receiptPath);
            document = JObject.Parse(json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("The modern data-root migration receipt is unreadable or corrupt; explicit recovery is required.", exception);
        }

        var schemaVersion = document.Value<int?>("schemaVersion")
            ?? throw new InvalidDataException("The modern data-root migration receipt has no schema version.");
        if (schemaVersion == 1)
        {
            ArchiveLegacyReceipt(receiptPath, Path.Combine(migrationDirectory, LegacyReceiptArchiveFileName), json);
            return null;
        }
        if (schemaVersion != ReceiptSchemaVersion) throw new InvalidDataException($"Unsupported modern data-root migration receipt schema {schemaVersion}.");

        DataRootMigrationReceipt receipt;
        try
        {
            receipt = document.ToObject<DataRootMigrationReceipt>(JsonSerializer.Create(SerializerSettings))
                ?? throw new InvalidDataException("The modern data-root migration receipt is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The modern data-root migration receipt is invalid; explicit recovery is required.", exception);
        }

        ValidateReceipt(receipt, former, current, sourceRoot, currentRoot);
        return receipt;
    }

    private static void ArchiveLegacyReceipt(string receiptPath, string archivePath, string json)
    {
        if (File.Exists(archivePath))
        {
            if (!string.Equals(File.ReadAllText(archivePath), json, StringComparison.Ordinal))
                throw new InvalidDataException("A different archived schema-1 migration receipt already exists; explicit recovery is required.");
            File.Delete(receiptPath);
            return;
        }

        File.Move(receiptPath, archivePath, overwrite: false);
    }

    private static void ValidateReceipt(
        DataRootMigrationReceipt receipt,
        IApplicationPaths former,
        IApplicationPaths current,
        DataRootIdentity sourceRoot,
        DataRootIdentity currentRoot)
    {
        if (receipt.SchemaVersion != ReceiptSchemaVersion || receipt.MigrationId != MigrationId ||
            receipt.ExpectedManifestVersion != ManifestVersion || receipt.ExpectedManifestCount != Manifest.Length)
            throw new InvalidDataException("The modern data-root migration receipt does not describe the expected migration manifest.");
        if (receipt.SourceRoot != sourceRoot || receipt.CurrentRoot != currentRoot)
            throw new InvalidDataException("The modern data-root migration receipt belongs to different source or destination roots.");
        if (!Enum.IsDefined(receipt.Status) || receipt.StartedAtUtc == default || receipt.UpdatedAtUtc < receipt.StartedAtUtc || receipt.Items is null)
            throw new InvalidDataException("The modern data-root migration receipt has invalid timestamps or progress.");

        var expected = Manifest.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in receipt.Items)
        {
            if (item is null || !seen.Add(item.Id) || !expected.TryGetValue(item.Id, out var entry) ||
                item.Durable != entry.Durable || !Enum.IsDefined(item.Outcome) || item.CompletedAtUtc < receipt.StartedAtUtc ||
                !PathsEqual(item.Source, entry.Path(former)) || !PathsEqual(item.Destination, entry.Path(current)))
                throw new InvalidDataException("The modern data-root migration receipt contains invalid item progress.");
        }

        var terminal = receipt.Status is not DataRootMigrationStatus.Started;
        if (terminal != receipt.CompletedAtUtc.HasValue || terminal && receipt.Items.Count != Manifest.Length)
            throw new InvalidDataException("A partial modern data-root migration receipt cannot claim a terminal status.");
        if (receipt.Status == DataRootMigrationStatus.Completed && receipt.Items.Any(item => item.Outcome == DataRootMigrationOutcome.Conflict || item.Durable && item.Outcome == DataRootMigrationOutcome.Failed) ||
            receipt.Status == DataRootMigrationStatus.CompletedWithConflicts && !receipt.Items.Any(item => item.Outcome == DataRootMigrationOutcome.Conflict) ||
            receipt.Status == DataRootMigrationStatus.Failed && !receipt.Items.Any(item => item.Durable && item.Outcome == DataRootMigrationOutcome.Failed))
            throw new InvalidDataException("The modern data-root migration receipt status does not match its item results.");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static DataRootMigrationReceipt CreateStartedReceipt(DataRootIdentity sourceRoot, DataRootIdentity currentRoot)
    {
        var now = DateTimeOffset.UtcNow;
        return new(ReceiptSchemaVersion, MigrationId, ManifestVersion, Manifest.Length, sourceRoot, currentRoot,
            DataRootMigrationStatus.Started, now, null, now, []);
    }

    private static DataRootMigrationReceipt BuildProgress(DataRootMigrationReceipt receipt, IEnumerable<DataRootMigrationItem> items, bool completed)
    {
        var ordered = Manifest.Select(entry => items.SingleOrDefault(item => item.Id == entry.Id)).Where(item => item is not null).Cast<DataRootMigrationItem>().ToArray();
        var now = DateTimeOffset.UtcNow;
        if (!completed) return receipt with { Status = DataRootMigrationStatus.Started, CompletedAtUtc = null, UpdatedAtUtc = now, Items = ordered };

        if (ordered.Length != Manifest.Length) throw new InvalidDataException("Migration cannot complete before every manifest item has progress.");
        var status = ordered.Any(item => item.Durable && item.Outcome == DataRootMigrationOutcome.Failed)
            ? DataRootMigrationStatus.Failed
            : ordered.Any(item => item.Outcome == DataRootMigrationOutcome.Conflict)
                ? DataRootMigrationStatus.CompletedWithConflicts
                : DataRootMigrationStatus.Completed;
        return receipt with { Status = status, CompletedAtUtc = now, UpdatedAtUtc = now, Items = ordered };
    }

    private static DataRootMigrationReceipt VerifyCompletedCopies(
        DataRootMigrationReceipt receipt,
        IApplicationPaths former,
        IApplicationPaths current,
        CancellationToken cancellationToken)
    {
        var changed = false;
        var items = receipt.Items.ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (item.Outcome is not (DataRootMigrationOutcome.Copied or DataRootMigrationOutcome.AlreadyPresent)) continue;

            var entry = Manifest.Single(candidate => candidate.Id == item.Id);
            var destination = TryHashRegular(item.Destination, entry.Root(current), cancellationToken);
            if (destination is not null && destination.Value.Hash == item.Sha256 && destination.Value.Length == item.Length) continue;

            var source = TryHashRegular(item.Source, entry.Root(former), cancellationToken);
            var sourceIsValid = source is not null && source.Value.Hash == item.Sha256 && source.Value.Length == item.Length;
            var message = sourceIsValid
                ? "Destination changed after verified migration while the preserved source remains valid; explicit recovery is required and the destination was not overwritten."
                : "A previously verified migration destination changed; explicit recovery is required and the destination was not overwritten.";
            items[index] = item with
            {
                Outcome = sourceIsValid ? DataRootMigrationOutcome.Conflict : DataRootMigrationOutcome.Failed,
                Sha256 = destination?.Hash,
                Length = destination?.Length,
                Message = message,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            changed = true;
        }

        return changed ? BuildProgress(receipt, items, completed: true) : receipt;
    }

    private static DataRootMigrationItem Process(
        string id,
        string source,
        string sourceRoot,
        string destination,
        string destinationRoot,
        bool durable,
        CancellationToken cancellationToken)
    {
        var sourceExists = File.Exists(source);
        var destinationExists = File.Exists(destination);
        if (!sourceExists && !destinationExists) return Item(DataRootMigrationOutcome.SourceMissing);
        if (!sourceExists)
        {
            EnsureRegularFile(destination, destinationRoot);
            var target = Hash(destination, cancellationToken);
            return Item(DataRootMigrationOutcome.DestinationOnly, target.Hash, target.Length);
        }

        EnsureRegularFile(source, sourceRoot);
        var sourceHash = Hash(source, cancellationToken);
        if (destinationExists)
        {
            EnsureRegularFile(destination, destinationRoot);
            var destinationHash = Hash(destination, cancellationToken);
            return sourceHash == destinationHash
                ? Item(DataRootMigrationOutcome.AlreadyPresent, destinationHash.Hash, destinationHash.Length)
                : Item(DataRootMigrationOutcome.Conflict, destinationHash.Hash, destinationHash.Length, "Destination retained; former-root file differs and explicit recovery is required.");
        }

        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidDataException("Migration destination has no parent directory.");
        Directory.CreateDirectory(parent);
        EnsureSafeDirectory(parent, destinationRoot);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.aaml-migrate-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            var copied = Hash(temporary, cancellationToken);
            if (copied != sourceHash) throw new IOException("Copied migration file failed hash verification.");
            File.Move(temporary, destination, false);
            var destinationHash = Hash(destination, cancellationToken);
            if (destinationHash != sourceHash) throw new IOException("Migration destination failed post-copy hash verification.");
            return Item(DataRootMigrationOutcome.Copied, destinationHash.Hash, destinationHash.Length);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        DataRootMigrationItem Item(DataRootMigrationOutcome outcome, string? hash = null, long? length = null, string? message = null) =>
            new(id, source, destination, durable, outcome, hash, length, message, DateTimeOffset.UtcNow);
    }

    private static void WriteReceipt(string directory, DataRootMigrationReceipt receipt)
    {
        var destination = Path.Combine(directory, ReceiptFileName);
        var temporary = Path.Combine(directory, $".{MigrationId}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonConvert.SerializeObject(receipt, SerializerSettings));
                writer.Write(Environment.NewLine);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static DataRootIdentity Identify(IApplicationPaths paths) => new(
        Path.GetFullPath(paths.ConfigurationDirectory),
        Path.GetFullPath(paths.DataDirectory),
        Path.GetFullPath(paths.StateDirectory));

    private static (string Hash, long Length)? TryHashRegular(string path, string root, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            EnsureRegularFile(path, root);
            return Hash(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static (string Hash, long Length) Hash(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            length += read;
        }
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }

    private static void EnsureRegularFile(string path, string root)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory))
            throw new InvalidDataException("Migration source or destination must be a regular file.");
        EnsureSafeDirectory(Path.GetDirectoryName(path)!, root);
    }

    private static void EnsureSafeDirectory(string path, string root)
    {
        for (var current = new DirectoryInfo(path); current is not null && current.Exists; current = current.Parent)
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Application-owned migration paths cannot traverse reparse points.");
            if (Path.GetFullPath(current.FullName).Equals(Path.GetFullPath(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
        }
        throw new InvalidDataException("Migration path is outside its application-owned root.");
    }
}
